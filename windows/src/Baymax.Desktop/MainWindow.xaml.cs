using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Baymax.Contracts;

namespace Baymax.Desktop;

public partial class MainWindow : Window
{
    private const int MaximumWorkspaceSuggestionFiles = 3000;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        UseDefaultCredentials = true
    })
    {
        BaseAddress = new Uri("http://127.0.0.1:5080/")
    };
    private readonly ObservableCollection<ChatSession> _sessions = [];
    private readonly SemaphoreSlim _historySaveLock = new(1, 1);
    private readonly DispatcherTimer _mobileTaskRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };
    private ChatSession? _currentChat;
    private bool _modelHealthy;
    private bool _settingsAvailable = true;
    private bool _planningEnabled = true;
    private bool _executionEnabled = true;
    private string _workspacePath = string.Empty;
    private string? _settingsError;
    private bool _busy;
    private readonly AgentHostProcessManager _agentHostProcessManager = new();
    private bool _allowWindowClose;
    private bool _shutdownPromptActive;
    private bool _mobileTaskSyncInProgress;
    private string? _lastMobileTaskSyncError;
    private IReadOnlyList<WorkspaceFileSuggestion> _workspaceFileIndex = [];
    private Task<IReadOnlyList<WorkspaceFileSuggestion>>? _workspaceFileIndexRefreshTask;
    private string? _workspaceFileIndexRefreshRoot;
    private string? _workspaceFileIndexRoot;
    private DateTimeOffset _workspaceFileIndexUpdatedAt = DateTimeOffset.MinValue;
    private int _fileSuggestionRequestId;
    private int _fileMentionStart;
    private int _fileMentionEnd;

    public MainWindow()
    {
        InitializeComponent();
        _mobileTaskRefreshTimer.Tick += MobileTaskRefreshTimer_Tick;
        IdentityText.Text = $"Windows account: {Environment.UserDomainName}\\{Environment.UserName}";
        ChatsListBox.ItemsSource = _sessions;
        RefreshSettingsDisplay();
        LoadChats();
        if (_currentChat is null)
        {
            CreateNewChat();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
        await TrySyncMobileTaskChatsAsync();
        _mobileTaskRefreshTimer.Start();
    }

    private async void MobileTaskRefreshTimer_Tick(object? sender, EventArgs e) =>
        await TrySyncMobileTaskChatsAsync();

    private async Task TrySyncMobileTaskChatsAsync()
    {
        if (_mobileTaskSyncInProgress)
        {
            return;
        }

        _mobileTaskSyncInProgress = true;
        try
        {
            var tasks = await _httpClient.GetFromJsonAsync<List<TaskResponse>>(
                "desktop/mobile/tasks",
                JsonOptions)
                ?? throw new InvalidDataException("Baymax returned an empty mobile task list.");
            _lastMobileTaskSyncError = null;
            await UpdateMobileTaskChatsAsync(tasks);
        }
        catch (Exception exception) when (IsRequestException(exception))
        {
            if (!_busy && !string.Equals(_lastMobileTaskSyncError, exception.Message, StringComparison.Ordinal))
            {
                StatusText.Text = $"Android task updates could not be refreshed: {exception.Message}";
            }

            _lastMobileTaskSyncError = exception.Message;
        }
        finally
        {
            _mobileTaskSyncInProgress = false;
        }
    }

    private async Task UpdateMobileTaskChatsAsync(IReadOnlyList<TaskResponse> tasks)
    {
        var changed = false;
        ChatSession? latestChangedChat = null;
        foreach (var task in tasks.OrderBy(task => task.CreatedAt))
        {
            var chat = _sessions.FirstOrDefault(session => session.MobileTaskId == task.TaskId);
            if (chat is null)
            {
                chat = new ChatSession
                {
                    MobileTaskId = task.TaskId,
                    Title = CreateMobileTaskTitle(task.UserInstruction)
                };
                chat.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = task.UserInstruction
                });
                _sessions.Insert(0, chat);
                changed = true;
            }

            var newEvents = task.Events
                .Where(taskEvent => taskEvent.Sequence > chat.LastMobileTaskEventSequence)
                .OrderBy(taskEvent => taskEvent.Sequence)
                .ToArray();
            foreach (var taskEvent in newEvents)
            {
                chat.Messages.Add(CreateMobileTaskEventMessage(taskEvent, task.Status));
                chat.LastMobileTaskEventSequence = taskEvent.Sequence;
                changed = true;
            }

            var status = $"Android task · {task.Status}";
            if (!string.Equals(chat.MobileTaskStatus, status, StringComparison.Ordinal))
            {
                chat.MobileTaskStatus = status;
                changed = true;
            }

            if (newEvents.Length > 0)
            {
                chat.UpdatedAt = task.UpdatedAt;
                MoveChatToTopWithoutSelecting(chat);
                latestChangedChat = chat;
            }
        }

        if (!changed)
        {
            return;
        }

        if (latestChangedChat is not null && !_busy)
        {
            StatusText.Text =
                $"{latestChangedChat.MobileTaskStatus} on this PC. Open \"{latestChangedChat.Title}\" in Recent chats.";
        }

        await PersistChatsAsync();
        if (ReferenceEquals(_currentChat, latestChangedChat))
        {
            ScrollChatToEnd();
        }
    }

    private static ChatMessage CreateMobileTaskEventMessage(TaskEventResponse taskEvent, TaskState taskStatus)
    {
        using var document = JsonDocument.Parse(taskEvent.PayloadJson);
        var payload = document.RootElement;
        return taskEvent.EventType switch
        {
            "TASK_RECEIVED" => new ChatMessage
            {
                Role = "assistant",
                Content = "Task received from Android."
            },
            "TASK_QUEUED" => new ChatMessage
            {
                Role = "assistant",
                Content = "Task queued on this PC."
            },
            "TASK_STARTED" => new ChatMessage
            {
                Role = "assistant",
                Content = "Task execution started on this PC."
            },
            "TASK_EXECUTION_RESULT" => CreateMobileTaskResultMessage(payload, taskStatus),
            "TASK_EXECUTION_FAILED" => new ChatMessage
            {
                Role = "assistant",
                Content = FormatMobileTaskFailure(payload)
            },
            _ => new ChatMessage
            {
                Role = "assistant",
                Content = FormatMobileTaskUpdate(taskEvent.EventType, payload)
            }
        };
    }

    private static ChatMessage CreateMobileTaskResultMessage(JsonElement payload, TaskState taskStatus)
    {
        var summary = GetString(payload, "summary");
        var status = taskStatus switch
        {
            TaskState.Completed => "Task completed on this PC.",
            TaskState.Partial => "Task partially completed on this PC.",
            TaskState.Failed => "Task execution failed on this PC.",
            _ => $"Task finished on this PC with status {taskStatus}."
        };
        var content = string.IsNullOrWhiteSpace(summary) ? status : $"{status} {summary}";
        var result = payload.TryGetProperty("execution", out var execution)
            ? JsonSerializer.Serialize(execution, PrettyJsonOptions)
            : null;
        return new ChatMessage
        {
            Role = "assistant",
            Content = content,
            Result = result
        };
    }

    private static string FormatMobileTaskFailure(JsonElement payload)
    {
        var code = GetString(payload, "code");
        var message = GetString(payload, "message");
        return string.IsNullOrWhiteSpace(code)
            ? $"Task failed on this PC: {message}"
            : $"Task failed on this PC ({code}): {message}";
    }

    private static string FormatMobileTaskUpdate(string eventType, JsonElement payload)
    {
        var message = GetString(payload, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            message = GetString(payload, "reason");
        }

        return string.IsNullOrWhiteSpace(message)
            ? $"Android task update: {eventType.Replace('_', ' ').ToLowerInvariant()}."
            : $"Android task update: {message}";
    }

    private static string CreateMobileTaskTitle(string instruction)
    {
        const int maximumLength = 42;
        var title = $"Android: {instruction}";
        return title.Length > maximumLength
            ? $"{title[..(maximumLength - 3)]}..."
            : title;
    }

    private void MoveChatToTopWithoutSelecting(ChatSession chat)
    {
        var index = _sessions.IndexOf(chat);
        if (index > 0)
        {
            _sessions.Move(index, 0);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshStatusAsync();

    private async void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        await OpenSettingsAsync();

    private async void PairAndroidButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Checking secure Android pairing...");
        try
        {
            var pairingStatus = await _httpClient.GetFromJsonAsync<PairingStatusResponse>(
                "pairing/status",
                JsonOptions)
                ?? throw new InvalidDataException("Baymax returned an empty pairing status.");
            if (!pairingStatus.Enabled)
            {
                var answer = MessageBox.Show(
                    this,
                    "Android pairing is disabled. Configure it in Settings, then start or restart Baymax.",
                    "Enable secure pairing?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                StatusText.Text = "Android pairing is disabled. The API remains loopback-only.";
                if (answer == MessageBoxResult.Yes)
                {
                    await OpenSettingsAsync();
                }

                return;
            }

            using var invitation = await _httpClient.PostAsync("pairing/invitations", content: null);
            if (!invitation.IsSuccessStatusCode)
            {
                throw await CreateRequestExceptionAsync(invitation);
            }

            var invitationResponse = await invitation.Content.ReadFromJsonAsync<PairingInvitationResponse>(JsonOptions)
                ?? throw new InvalidDataException("Baymax returned an empty pairing invitation.");
            new PairingWindow(_httpClient, invitationResponse)
            {
                Owner = this
            }.ShowDialog();
            StatusText.Text = "Android pairing window closed.";
        }
        catch (Exception exception) when (IsRequestException(exception))
        {
            ShowRequestError("Could not start Android pairing", exception);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private async void EnterButton_Click(object sender, RoutedEventArgs e) =>
        await SendMessageAsync();

    private async void InstructionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var requestId = ++_fileSuggestionRequestId;
        if (!TryGetCurrentFileMention(
                InstructionTextBox.Text,
                InstructionTextBox.CaretIndex,
                out var mentionStart,
                out var mentionEnd,
                out var query))
        {
            FileSuggestionsPopup.IsOpen = false;
            return;
        }

        try
        {
            var files = await GetWorkspaceFileIndexAsync();
            if (requestId != _fileSuggestionRequestId
                || !TryGetCurrentFileMention(
                    InstructionTextBox.Text,
                    InstructionTextBox.CaretIndex,
                    out mentionStart,
                    out mentionEnd,
                    out query))
            {
                return;
            }

            var matches = files
                .Where(file => file.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.RelativePath.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            _fileMentionStart = mentionStart;
            _fileMentionEnd = mentionEnd;
            FileSuggestionsListBox.ItemsSource = matches;
            FileSuggestionsListBox.SelectedIndex = matches.Length > 0 ? 0 : -1;
            FileSuggestionsPopup.IsOpen = matches.Length > 0;
        }
        catch (Exception exception) when (exception is IOException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                            or NotSupportedException)
        {
            if (requestId == _fileSuggestionRequestId)
            {
                FileSuggestionsPopup.IsOpen = false;
                StatusText.Text = $"Workspace file suggestions failed: {exception.Message}";
            }
        }
    }

    private async void InstructionTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FileSuggestionsPopup.IsOpen)
        {
            FileSuggestionsPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (FileSuggestionsPopup.IsOpen
            && (e.Key == Key.Tab || e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            && FileSuggestionsListBox.SelectedItem is WorkspaceFileSuggestion suggestion)
        {
            UseFileSuggestion(suggestion);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsAvailable)
        {
            StatusText.Text = _settingsError ?? "Choose a workspace before attaching a file.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Attach a file from the Baymax workspace",
            InitialDirectory = Directory.Exists(_workspacePath)
                ? _workspacePath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var filePath = Path.GetFullPath(dialog.FileName);
            if (!IsFileWithinWorkspace(filePath, _workspacePath))
            {
                MessageBox.Show(
                    this,
                    "Choose a file located inside the current Baymax workspace.",
                    "File is outside the workspace",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            InsertTextAtCaret($"[Attached workspace file: \"{filePath}\"]");
        }
        catch (Exception exception) when (exception is IOException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                            or NotSupportedException)
        {
            StatusText.Text = $"The selected file could not be attached: {exception.Message}";
        }
    }

    private void FileSuggestionListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FileSuggestionsListBox.SelectedItem is WorkspaceFileSuggestion suggestion)
        {
            UseFileSuggestion(suggestion);
            e.Handled = true;
        }
    }

    private void UseFileSuggestion(WorkspaceFileSuggestion suggestion)
    {
        var text = InstructionTextBox.Text;
        if (_fileMentionStart < 0
            || _fileMentionEnd < _fileMentionStart
            || _fileMentionEnd > text.Length)
        {
            FileSuggestionsPopup.IsOpen = false;
            return;
        }

        var replacement = $"@\"{suggestion.FullPath}\"";
        InstructionTextBox.Text = text
            .Remove(_fileMentionStart, _fileMentionEnd - _fileMentionStart)
            .Insert(_fileMentionStart, replacement);
        InstructionTextBox.CaretIndex = _fileMentionStart + replacement.Length;
        InstructionTextBox.Focus();
        FileSuggestionsPopup.IsOpen = false;
        ++_fileSuggestionRequestId;
    }

    private void InsertTextAtCaret(string value)
    {
        var text = InstructionTextBox.Text;
        var caret = InstructionTextBox.CaretIndex;
        var prefix = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : string.Empty;
        var suffix = caret < text.Length && !char.IsWhiteSpace(text[caret]) ? " " : string.Empty;
        var insertion = $"{prefix}{value}{suffix}";
        InstructionTextBox.Text = text.Insert(caret, insertion);
        InstructionTextBox.CaretIndex = caret + insertion.Length;
        InstructionTextBox.Focus();
        FileSuggestionsPopup.IsOpen = false;
        ++_fileSuggestionRequestId;
    }

    private async Task<IReadOnlyList<WorkspaceFileSuggestion>> GetWorkspaceFileIndexAsync()
    {
        var workspaceRoot = Path.GetFullPath(_workspacePath);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"Baymax workspace does not exist: {workspaceRoot}");
        }

        if (string.Equals(_workspaceFileIndexRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - _workspaceFileIndexUpdatedAt < TimeSpan.FromSeconds(30))
        {
            return _workspaceFileIndex;
        }

        if (_workspaceFileIndexRefreshTask is null
            || !string.Equals(_workspaceFileIndexRefreshRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            _workspaceFileIndexRefreshRoot = workspaceRoot;
            _workspaceFileIndexRefreshTask = Task.Run(() => EnumerateWorkspaceFiles(workspaceRoot));
        }

        var refreshTask = _workspaceFileIndexRefreshTask;
        try
        {
            var files = await refreshTask;
            if (string.Equals(_workspacePath, workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                _workspaceFileIndex = files;
                _workspaceFileIndexRoot = workspaceRoot;
                _workspaceFileIndexUpdatedAt = DateTimeOffset.UtcNow;
            }

            return files;
        }
        finally
        {
            if (ReferenceEquals(_workspaceFileIndexRefreshTask, refreshTask))
            {
                _workspaceFileIndexRefreshTask = null;
            }
        }
    }

    private static IReadOnlyList<WorkspaceFileSuggestion> EnumerateWorkspaceFiles(string workspaceRoot)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 8
        };
        var files = new List<WorkspaceFileSuggestion>();
        foreach (var filePath in Directory.EnumerateFiles(workspaceRoot, "*", options))
        {
            files.Add(new WorkspaceFileSuggestion(
                Path.GetRelativePath(workspaceRoot, filePath),
                filePath));
            if (files.Count >= MaximumWorkspaceSuggestionFiles)
            {
                break;
            }
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void InvalidateWorkspaceFileIndex()
    {
        ++_fileSuggestionRequestId;
        _workspaceFileIndex = [];
        _workspaceFileIndexRoot = null;
        _workspaceFileIndexUpdatedAt = DateTimeOffset.MinValue;
        _workspaceFileIndexRefreshTask = null;
        _workspaceFileIndexRefreshRoot = null;
        FileSuggestionsPopup.IsOpen = false;
    }

    private static bool TryGetCurrentFileMention(
        string text,
        int caret,
        out int mentionStart,
        out int mentionEnd,
        out string query)
    {
        mentionStart = -1;
        mentionEnd = -1;
        query = string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        if (caret == 0)
        {
            return false;
        }

        var atIndex = text.LastIndexOf('@', caret - 1);
        if (atIndex < 0
            || atIndex > 0 && char.IsLetterOrDigit(text[atIndex - 1])
            || text.AsSpan(atIndex + 1, caret - atIndex - 1).IndexOfAny('\r', '\n', '"') >= 0)
        {
            return false;
        }

        mentionStart = atIndex;
        mentionEnd = caret;
        query = text[(atIndex + 1)..caret];
        return true;
    }

    private static bool IsFileWithinWorkspace(string filePath, string workspacePath)
    {
        var fullFilePath = Path.GetFullPath(filePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var workspacePrefix = fullWorkspacePath.EndsWith(Path.DirectorySeparatorChar)
            ? fullWorkspacePath
            : fullWorkspacePath + Path.DirectorySeparatorChar;
        return fullFilePath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendMessageAsync()
    {
        var instruction = InstructionTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return;
        }

        if (instruction.Length > 4000)
        {
            StatusText.Text = "Messages must be 4,000 characters or fewer.";
            return;
        }

        if (_currentChat is null)
        {
            CreateNewChat();
        }

        var history = BuildConversationHistory(_currentChat!);
        var userMessage = new ChatMessage { Role = "user", Content = instruction };
        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "Thinking...",
            IsPending = true
        };
        _currentChat!.Messages.Add(userMessage);
        _currentChat.Messages.Add(assistantMessage);
        _currentChat.UpdatedAt = DateTimeOffset.UtcNow;
        if (_currentChat.Messages.Count == 2 && _currentChat.Title == "New chat")
        {
            _currentChat.Title = instruction.Length > 42 ? $"{instruction[..39]}..." : instruction;
        }

        MoveChatToTop(_currentChat);
        InstructionTextBox.Clear();
        ScrollChatToEnd();
        await PersistChatsAsync();
        SetBusy(true, "Asking Baymax to prepare a plan...");
        try
        {
            var plan = await PostAsync<CreatePlanRequest, TaskPlanResponse>(
                "plans",
                new CreatePlanRequest(instruction, history));
            assistantMessage.IsPending = false;
            assistantMessage.Plan = plan;
            assistantMessage.Content = plan.NeedsClarification
                ? $"Baymax needs one more detail: {plan.ClarifyingQuestion}"
                : "Here is the plan. Review the actions before approving.";
            StatusText.Text = plan.NeedsClarification
                ? "Baymax needs clarification before preparing actions."
                : _executionEnabled
                    ? "Review the plan, edit it if needed, then approve."
                    : "Plan prepared, but execution is disabled in Settings.";
        }
        catch (Exception exception) when (IsRequestException(exception))
        {
            assistantMessage.IsPending = false;
            assistantMessage.Content = $"I could not prepare a plan: {exception.Message}";
            StatusText.Text = "Plan could not be created.";
        }
        finally
        {
            _currentChat.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistChatsAsync();
            ScrollChatToEnd();
            SetBusy(false, StatusText.Text);
        }
    }

    private async void EditPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message
            || message.Plan is null
            || message.PlanApproved)
        {
            return;
        }

        var editor = new PlanEditorWindow(message.Plan) { Owner = this };
        if (editor.ShowDialog() != true || editor.EditedPlan is null)
        {
            return;
        }

        message.Plan = editor.EditedPlan;
        message.Content = "Plan edited. Review the updated actions before approving.";
        StatusText.Text = "Edited plan will be validated by Baymax when approved.";
        _currentChat!.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistChatsAsync();
        ScrollChatToEnd();
    }

    private async void ApprovePlanButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message
            || message.Plan is null
            || message.Plan.NeedsClarification
            || message.Plan.Actions.Count == 0
            || message.PlanApproved
            || !_executionEnabled)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            BuildConfirmationMessage(message.Plan),
            "Approve and run this plan?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            StatusText.Text = "Plan was not approved. No filesystem action was run.";
            return;
        }

        SetBusy(true, "Requesting Windows-authenticated approval...");
        var approvalIssued = false;
        try
        {
            var approval = await PostAsync<CreatePlanApprovalRequest, PlanApprovalResponse>(
                "plans/approvals",
                new CreatePlanApprovalRequest(message.Plan, ConfirmApproval: true));
            approvalIssued = true;
            message.PlanApproved = true;
            message.Content = $"Approved by {approval.ApprovedBy}. Running the reviewed plan...";
            await PersistChatsAsync();

            var execution = await PostAsync<ExecutePlanRequest, PlanExecutionResponse>(
                "plans/execute",
                new ExecutePlanRequest(message.Plan, approval.ApprovalId));
            message.Result = FormatExecution(execution);
            message.Content = $"Execution finished with status: {execution.Status}.";
            StatusText.Text = message.Content;
        }
        catch (Exception exception) when (IsRequestException(exception))
        {
            message.Content = $"Baymax could not execute the plan: {exception.Message}";
            StatusText.Text = approvalIssued
                ? "Approval was issued and may have been consumed. Review the error before creating a new plan."
                : "Plan approval was rejected.";
        }
        finally
        {
            _currentChat!.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistChatsAsync();
            ScrollChatToEnd();
            SetBusy(false, StatusText.Text);
        }
    }

    private async void DiscardPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message || message.PlanApproved)
        {
            return;
        }

        message.Plan = null;
        message.Content = "Plan discarded. No filesystem action was run.";
        _currentChat!.UpdatedAt = DateTimeOffset.UtcNow;
        StatusText.Text = message.Content;
        await PersistChatsAsync();
    }

    private void NewChatButton_Click(object sender, RoutedEventArgs e) => CreateNewChat();

    private async void DeleteChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not ChatSession chat)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Delete \"{chat.Title}\" and all its saved messages? This cannot be undone.",
            "Delete chat",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var deletingCurrentChat = ReferenceEquals(_currentChat, chat);
        var createdReplacementChat = false;
        if (deletingCurrentChat)
        {
            ChatsListBox.SelectedItem = null;
            _currentChat = null;
            ChatItemsControl.ItemsSource = null;
        }

        _sessions.Remove(chat);
        if (deletingCurrentChat)
        {
            if (_sessions.Count > 0)
            {
                ChatsListBox.SelectedItem = _sessions[0];
            }
            else
            {
                CreateNewChat();
                createdReplacementChat = true;
            }
        }

        await PersistChatsAsync();
        StatusText.Text = createdReplacementChat
            ? "Chat deleted. A new chat is ready."
            : "Chat deleted.";
    }

    private void ChatsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChatsListBox.SelectedItem is ChatSession selected)
        {
            _currentChat = selected;
            ChatItemsControl.ItemsSource = selected.Messages;
            ScrollChatToEnd();
        }
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        InstructionTextBox.Clear();
        if (_currentChat is null || _currentChat.Messages.Count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Clear all messages and plans in this chat? This cannot be undone.",
            "Clear chat",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _currentChat.Messages.Clear();
        _currentChat.Title = "New chat";
        _currentChat.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistChatsAsync();
        StatusText.Text = "Chat cleared.";
    }

    private async Task RefreshStatusAsync()
    {
        SetBusy(true, "Checking Baymax and the local model...");
        try
        {
            using var healthResponse = await _httpClient.GetAsync("health");
            if (!healthResponse.IsSuccessStatusCode)
            {
                throw await CreateRequestExceptionAsync(healthResponse);
            }

            var modelStatus = await _httpClient.GetFromJsonAsync<ModelRuntimeStatus>("model/status", JsonOptions)
                ?? throw new InvalidDataException("Baymax returned an empty model status.");
            _modelHealthy = string.Equals(modelStatus.Status, "healthy", StringComparison.OrdinalIgnoreCase);
            ModelStatusText.Text = _modelHealthy
                ? "Local model ready"
                : $"Model {modelStatus.Status}: {modelStatus.Message}";
            ModelStatusIndicator.Fill = _modelHealthy
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                : new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StatusText.Text = _modelHealthy
                ? !_settingsAvailable
                    ? _settingsError ?? "Workspace settings are unavailable."
                    : !_planningEnabled
                        ? "Workspace actions are disabled. Enable Read or Write in Settings."
                        : "Ready. Continue this chat or start a new one."
                : "Baymax is running, but planning needs a healthy local model.";
        }
        catch (Exception exception) when (IsRequestException(exception))
        {
            _modelHealthy = false;
            ModelStatusText.Text = "Baymax is offline";
            ModelStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            StatusText.Text = $"Baymax status check failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private async Task OpenSettingsAsync()
    {
        DesktopSettings settings;
        try
        {
            settings = DesktopSettingsStore.Load();
        }
        catch (Exception exception) when (exception is IOException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                            or NotSupportedException)
        {
            MessageBox.Show(this, exception.Message, "Could not load Baymax settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        new SettingsWindow(settings, _agentHostProcessManager)
        {
            Owner = this
        }.ShowDialog();
        RefreshSettingsDisplay();
        await RefreshStatusAsync();
    }

    private void RefreshSettingsDisplay()
    {
        try
        {
            var settings = DesktopSettingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.WorkspacePath)
                || !Path.IsPathFullyQualified(settings.WorkspacePath))
            {
                throw new InvalidDataException("The saved workspace path is missing or is not absolute. Choose a valid folder in Settings.");
            }

            var previousWorkspacePath = _workspacePath;
            _workspacePath = Path.GetFullPath(settings.WorkspacePath);
            if (!string.Equals(previousWorkspacePath, _workspacePath, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateWorkspaceFileIndex();
            }

            _planningEnabled = settings.ReadEnabled || settings.WriteEnabled;
            _executionEnabled = settings.ExecuteEnabled;
            _settingsAvailable = true;
            _settingsError = null;
            WorkspaceText.Text = $"Workspace: {_workspacePath}";
        }
        catch (Exception exception) when (exception is IOException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                            or NotSupportedException)
        {
            _settingsAvailable = false;
            _planningEnabled = false;
            _executionEnabled = false;
            _settingsError = $"Workspace settings could not be loaded: {exception.Message}";
            WorkspaceText.Text = "Workspace: settings unavailable";
            StatusText.Text = _settingsError;
        }
    }

    private void LoadChats()
    {
        try
        {
            foreach (var session in ChatHistoryStore.Load().OrderByDescending(chat => chat.UpdatedAt))
            {
                _sessions.Add(session);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Could not load chat history",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_sessions.Count > 0)
        {
            ChatsListBox.SelectedItem = _sessions[0];
        }
    }

    private void CreateNewChat()
    {
        var session = new ChatSession();
        _sessions.Insert(0, session);
        ChatsListBox.SelectedItem = session;
        _currentChat = session;
        ChatItemsControl.ItemsSource = session.Messages;
        InstructionTextBox.Clear();
        StatusText.Text = "New chat. Enter a message to begin.";
        _ = PersistChatsAsync();
    }

    private IReadOnlyList<ConversationMessage> BuildConversationHistory(ChatSession chat)
    {
        var messages = chat.Messages
            .Where(message => !message.IsPending)
            .Select(message => new ConversationMessage(
                message.Role,
                TruncateContext(string.Join(
                    Environment.NewLine,
                    message.Content,
                    message.Plan is { NeedsClarification: false } plan
                        ? $"Plan summary: {plan.Summary}{Environment.NewLine}Actions: {string.Join("; ", plan.Actions.Select(action => $"{action.Name} {action.Arguments.GetRawText()}"))}"
                        : string.Empty,
                    message.Result ?? string.Empty))))
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .TakeLast(40)
            .ToList();

        while (messages.Count > 0 && messages.Sum(message => (long)message.Content.Length) > 16000)
        {
            messages.RemoveAt(0);
        }

        return messages;
    }

    private static string TruncateContext(string value) =>
        value.Length <= 4000 ? value : $"{value[..3997]}...";

    private void MoveChatToTop(ChatSession chat)
    {
        var index = _sessions.IndexOf(chat);
        if (index > 0)
        {
            _sessions.Move(index, 0);
        }

        ChatsListBox.SelectedItem = chat;
    }

    private async Task PersistChatsAsync()
    {
        await _historySaveLock.WaitAsync();
        try
        {
            await ChatHistoryStore.SaveAsync(_sessions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Chat history could not be saved: {exception.Message}";
        }
        finally
        {
            _historySaveLock.Release();
        }
    }

    private void ScrollChatToEnd() =>
        Dispatcher.BeginInvoke(ChatScrollViewer.ScrollToEnd, DispatcherPriority.Background);

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose || !_agentHostProcessManager.IsRunning)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownPromptActive)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Closing Baymax will stop the AgentHost and local model. This may interrupt active work.",
            "Stop Baymax and close?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _shutdownPromptActive = true;
        try
        {
            await _agentHostProcessManager.StopAsync();
            _allowWindowClose = true;
            Close();
        }
        catch (Exception exception) when (exception is HttpRequestException
                                            or InvalidOperationException
                                            or TimeoutException
                                            or OperationCanceledException)
        {
            MessageBox.Show(this, exception.Message, "Could not stop Baymax",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _shutdownPromptActive = false;
        }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _mobileTaskRefreshTimer.Stop();
        await PersistChatsAsync();
        _httpClient.Dispose();
        _historySaveLock.Dispose();
        _agentHostProcessManager.Dispose();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        EnterButton.IsEnabled = !busy && _modelHealthy && _settingsAvailable && _planningEnabled;
        InstructionTextBox.IsEnabled = !busy;
        AttachFileButton.IsEnabled = !busy && _settingsAvailable;
        PairAndroidButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        NewChatButton.IsEnabled = !busy;
        ChatsListBox.IsEnabled = !busy;
        StatusText.Text = status;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string endpoint, TRequest request)
    {
        using var response = await _httpClient.PostAsJsonAsync(endpoint, request, JsonOptions);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateRequestExceptionAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions)
            ?? throw new InvalidDataException($"Baymax returned an empty response from {endpoint}.");
    }

    private static async Task<HttpRequestException> CreateRequestExceptionAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var message = $"Baymax returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(detail.GetString()))
            {
                message = detail.GetString()!;
            }
            else if (document.RootElement.TryGetProperty("title", out var title)
                     && title.ValueKind == JsonValueKind.String)
            {
                message = title.GetString()!;
            }
        }
        catch (JsonException)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                message = body;
            }
        }

        return new HttpRequestException(message, null, response.StatusCode);
    }

    private static bool IsRequestException(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException;

    private void ShowRequestError(string title, Exception exception)
    {
        StatusText.Text = exception.Message;
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private string BuildConfirmationMessage(TaskPlanResponse plan)
    {
        var message = new StringBuilder()
            .AppendLine(plan.Summary)
            .AppendLine()
            .AppendLine($"Baymax will run {plan.Actions.Count} action(s) inside this workspace:")
            .AppendLine(_workspacePath)
            .AppendLine("Review the paths and any file content shown in the plan.")
            .AppendLine("This operation may change files and cannot always be undone.");
        if (plan.Actions.Any(action => action.Name == "MOVE_FILE"))
        {
            message.AppendLine("A move removes the file from its original path.");
        }

        if (plan.Actions.Any(action =>
                (action.Name == "WRITE_FILE" && GetString(action.Arguments, "mode") == "replace")
                || (action.Name is "MOVE_FILE" or "RENAME_PATH"
                    && GetBoolean(action.Arguments, "replaceExisting"))))
        {
            message.AppendLine("An approved replacement permanently replaces the destination's previous contents.");
        }

        message.AppendLine().Append("Do you want to approve and run this exact plan?");
        return message.ToString();
    }

    private static string FormatExecution(PlanExecutionResponse execution)
    {
        var output = new StringBuilder()
            .AppendLine($"Overall status: {execution.Status}")
            .AppendLine($"Finished: {execution.CompletedAt.LocalDateTime:G}")
            .AppendLine();
        foreach (var action in execution.Actions)
        {
            output.AppendLine($"{action.Ordinal}. {action.Name} — {action.Status}");
            if (!string.IsNullOrWhiteSpace(action.FailureCode))
            {
                output.AppendLine($"   {action.FailureCode}: {action.Error}");
            }

            if (action.Result is { ValueKind: JsonValueKind.Object } result)
            {
                if (result.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    output.AppendLine(content.GetString());
                }
                else
                {
                    output.AppendLine(result.GetRawText());
                }
            }

            output.AppendLine();
        }

        return output.ToString();
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : false;

    private sealed record WorkspaceFileSuggestion(string RelativePath, string FullPath);
}
