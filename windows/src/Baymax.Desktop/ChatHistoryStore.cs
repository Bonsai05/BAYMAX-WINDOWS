using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baymax.Contracts;

namespace Baymax.Desktop;

public sealed class ChatSession : INotifyPropertyChanged
{
    private string _title = "New chat";
    private string _mobileTaskStatus = string.Empty;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? MobileTaskId { get; set; }
    public int LastMobileTaskEventSequence { get; set; }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string MobileTaskStatus
    {
        get => _mobileTaskStatus;
        set => SetField(ref _mobileTaskStatus, value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set => SetField(ref _updatedAt, value);
    }

    public ObservableCollection<ChatMessage> Messages { get; set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private TaskPlanResponse? _plan;
    private string? _result;
    private bool _isPending;
    private bool _planApproved;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "assistant";

    public string Content
    {
        get => _content;
        set => SetField(ref _content, value);
    }

    public TaskPlanResponse? Plan
    {
        get => _plan;
        set
        {
            if (SetField(ref _plan, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlan)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlanActions)));
            }
        }
    }

    public string? Result
    {
        get => _result;
        set
        {
            if (SetField(ref _result, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResult)));
            }
        }
    }

    public bool IsPending
    {
        get => _isPending;
        set => SetField(ref _isPending, value);
    }

    public bool PlanApproved
    {
        get => _planApproved;
        set => SetField(ref _planApproved, value);
    }

    [JsonIgnore]
    public bool IsUser => Role == "user";

    [JsonIgnore]
    public bool HasPlan => Plan is not null;

    [JsonIgnore]
    public bool HasResult => !string.IsNullOrWhiteSpace(Result);

    [JsonIgnore]
    public IReadOnlyList<ChatPlanAction> PlanActions =>
        Plan?.Actions.Select(ChatPlanAction.FromAction).ToArray() ?? [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed record ChatPlanAction(string Heading, string Details, string Content, string Warning)
{
    public static ChatPlanAction FromAction(PlannedAction action)
    {
        var arguments = action.Arguments;
        var content = string.Empty;
        var warning = string.Empty;
        var details = action.Name switch
        {
            "LIST_DIRECTORY" =>
                $"Folder: {GetString(arguments, "path")}\nRecursive: {GetBoolean(arguments, "recursive")}, max depth {GetInteger(arguments, "maxDepth")}, max entries {GetInteger(arguments, "maxEntries")}",
            "SEARCH_FILES" =>
                $"Folder: {GetString(arguments, "rootPath")}\nName contains: {GetString(arguments, "query")}\nFile patterns: {GetStringArray(arguments, "filePatterns")}\nMaximum results: {GetInteger(arguments, "maxResults")}",
            "GET_FILE_METADATA" => $"Path: {GetString(arguments, "path")}",
            "READ_FILE" =>
                $"Path: {GetString(arguments, "path")}\nMaximum read size: {GetInteger(arguments, "maxBytes")} bytes",
            "WRITE_FILE" => FormatWrite(arguments, out content, out warning),
            "COPY_FILES" =>
                $"Files:\n{string.Join(Environment.NewLine, arguments.GetProperty("sourcePaths").EnumerateArray().Select(value => $"  • {value.GetString()}"))}\nDestination folder: {GetString(arguments, "destinationDirectory")}\nCollision rule: {GetString(arguments, "collisionPolicy")}\nMaximum total size: {GetInteger(arguments, "maxTotalBytes")} bytes",
            "MOVE_FILE" => FormatMove(arguments, out warning),
            "RENAME_PATH" => FormatRename(arguments, out warning),
            "CREATE_DIRECTORY" => $"New folder: {GetString(arguments, "path")}",
            _ => arguments.GetRawText()
        };

        return new ChatPlanAction(
            $"{action.Ordinal}. {FriendlyActionName(action.Name)}",
            details,
            content,
            warning);
    }

    private static string FormatWrite(JsonElement arguments, out string content, out string warning)
    {
        var mode = GetString(arguments, "mode");
        content = GetString(arguments, "content");
        warning = mode switch
        {
            "replace" => "REPLACE: existing file contents will be replaced.",
            "append" => "APPEND: text will be added to the existing file.",
            _ => string.Empty
        };
        return $"Mode: {mode}\nPath: {GetString(arguments, "path")}\nContent to write ({Encoding.UTF8.GetByteCount(content)} UTF-8 bytes):";
    }

    private static string FormatMove(JsonElement arguments, out string warning)
    {
        warning = GetBoolean(arguments, "replaceExisting")
            ? "MOVE AND REPLACE: destination file contents will be replaced; source will leave its old path."
            : "MOVE: file will leave its old path. Baymax will not permanently delete it.";
        return $"From: {GetString(arguments, "sourcePath")}\nTo: {GetString(arguments, "destinationPath")}";
    }

    private static string FormatRename(JsonElement arguments, out string warning)
    {
        warning = GetBoolean(arguments, "replaceExisting")
            ? "RENAME AND REPLACE: destination file contents will be replaced."
            : "The item will be renamed in the same folder.";
        return $"From: {GetString(arguments, "sourcePath")}\nTo: {GetString(arguments, "destinationPath")}";
    }

    private static string FriendlyActionName(string actionName) =>
        actionName switch
        {
            "LIST_DIRECTORY" => "List folder",
            "SEARCH_FILES" => "Search file names",
            "GET_FILE_METADATA" => "Get file details",
            "READ_FILE" => "Read text file",
            "WRITE_FILE" => "Write text file",
            "COPY_FILES" => "Copy files",
            "MOVE_FILE" => "Move file",
            "RENAME_PATH" => "Rename item",
            "CREATE_DIRECTORY" => "Create folder",
            _ => actionName
        };

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : false;

    private static string GetStringArray(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? string.Join(", ", value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()))
            : "any";

    private static long GetInteger(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
}

public sealed record EditablePlanAction(string Name, JsonElement Arguments);

public sealed record EditablePlanDocument(string Summary, IReadOnlyList<EditablePlanAction> Actions);

public static class ChatHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Baymax",
        "chat-history.json");

    public static IReadOnlyList<ChatSession> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ChatSession>>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new InvalidDataException("Baymax chat history is empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Baymax chat history could not be read from {FilePath}.", exception);
        }
    }

    public static async Task SaveAsync(
        IEnumerable<ChatSession> sessions,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The Baymax chat history directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, sessions, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
