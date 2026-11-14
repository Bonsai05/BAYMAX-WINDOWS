using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using Microsoft.Win32;

namespace Baymax.Desktop;

public partial class SettingsWindow : Window
{
    private readonly AgentHostProcessManager _processManager;
    private bool _busy;

    public SettingsWindow(DesktopSettings settings, AgentHostProcessManager processManager)
    {
        InitializeComponent();
        _processManager = processManager;
        WorkspacePathTextBox.Text = settings.WorkspacePath;
        ReadPermissionCheckBox.IsChecked = settings.ReadEnabled;
        WritePermissionCheckBox.IsChecked = settings.WriteEnabled;
        ExecutePermissionCheckBox.IsChecked = settings.ExecuteEnabled;
        AndroidPairingToggleButton.IsChecked = settings.AndroidPairingEnabled;
        MobileDirectExecutionCheckBox.IsChecked = settings.MobileDirectExecutionEnabled;
        MobileBindAddressTextBox.Text = settings.MobileBindAddress;
        MobilePortTextBox.Text = settings.MobilePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LlamaCppToggleButton.IsChecked = settings.LlamaCppEnabled;
        LlamaCppPortTextBox.Text = settings.LlamaCppPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ExecutablePathTextBox.Text = settings.LlamaCppExecutablePath;
        ModelPathTextBox.Text = settings.LlamaCppModelPath;
        GpuLayersTextBox.Text = settings.LlamaCppGpuLayers?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        SettingsStatusText.Text = $"Settings are saved in {DesktopSettingsStore.FilePath}. Changes take effect when Baymax starts or restarts.";
        UpdateServiceFields();
        UpdateHostButtons();
    }

    private void AndroidPairingToggleButton_Changed(object sender, RoutedEventArgs e)
    {
        AndroidPairingToggleButton.Content = AndroidPairingToggleButton.IsChecked == true ? "Enabled" : "Disabled";
        UpdateServiceFields();
    }

    private void LlamaCppToggleButton_Changed(object sender, RoutedEventArgs e)
    {
        LlamaCppToggleButton.Content = LlamaCppToggleButton.IsChecked == true ? "Enabled" : "Disabled";
        UpdateServiceFields();
    }

    private void BrowseExecutableButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the llama.cpp server executable",
            Filter = "Windows executable (llama-server.exe)|llama-server.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ExecutablePathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Baymax workspace",
            InitialDirectory = Directory.Exists(WorkspacePathTextBox.Text)
                ? WorkspacePathTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            WorkspacePathTextBox.Text = dialog.FolderName;
        }
    }

    private void BrowseModelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a GGUF model file",
            Filter = "GGUF model files (*.gguf)|*.gguf|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ModelPathTextBox.Text = dialog.FileName;
        }
    }

    private async void ApplyStartButton_Click(object sender, RoutedEventArgs e)
    {
        DesktopSettings settings;
        try
        {
            settings = ReadSettings();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            ShowSettingsError(exception.Message);
            return;
        }

        if (_processManager.IsRunning)
        {
            var answer = MessageBox.Show(
                this,
                "Baymax will stop and restart. This can interrupt work currently using the local model or filesystem executor.",
                "Restart Baymax?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        SetBusy(true, "Saving settings and starting Baymax...");
        try
        {
            await DesktopSettingsStore.SaveAsync(settings);
            if (_processManager.IsRunning)
            {
                await _processManager.StopAsync();
            }

            await _processManager.StartAsync(settings);
            SettingsStatusText.Text = settings.LlamaCppEnabled
                ? "Baymax is running. The local model is starting; check its status on the main screen."
                : "Baymax is running. The local model is disabled.";
        }
        catch (Exception exception) when (exception is HttpRequestException
                                            or InvalidOperationException
                                            or IOException
                                            or UnauthorizedAccessException
                                            or TimeoutException
                                            or System.ComponentModel.Win32Exception
                                            or OperationCanceledException)
        {
            ShowSettingsError(exception.Message);
        }
        finally
        {
            SetBusy(false, SettingsStatusText.Text);
            UpdateHostButtons();
        }
    }

    private async void StopBaymaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_processManager.IsRunning)
        {
            SettingsStatusText.Text = "Baymax is not running under Desktop management.";
            UpdateHostButtons();
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Stopping Baymax also stops the local model and may interrupt current work.",
            "Stop Baymax?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, "Stopping Baymax gracefully...");
        try
        {
            await _processManager.StopAsync();
            SettingsStatusText.Text = "Baymax and the local model have stopped.";
        }
        catch (Exception exception) when (exception is HttpRequestException
                                            or InvalidOperationException
                                            or TimeoutException
                                            or OperationCanceledException)
        {
            ShowSettingsError(exception.Message);
        }
        finally
        {
            SetBusy(false, SettingsStatusText.Text);
            UpdateHostButtons();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private DesktopSettings ReadSettings()
    {
        var settings = new DesktopSettings
        {
            WorkspacePath = ReadWorkspacePath(WorkspacePathTextBox.Text),
            ReadEnabled = ReadPermissionCheckBox.IsChecked == true,
            WriteEnabled = WritePermissionCheckBox.IsChecked == true,
            ExecuteEnabled = ExecutePermissionCheckBox.IsChecked == true,
            AndroidPairingEnabled = AndroidPairingToggleButton.IsChecked == true,
            MobileDirectExecutionEnabled = MobileDirectExecutionCheckBox.IsChecked == true,
            MobileBindAddress = MobileBindAddressTextBox.Text.Trim(),
            MobilePort = ReadPort(MobilePortTextBox.Text, "Mobile HTTPS port"),
            LlamaCppEnabled = LlamaCppToggleButton.IsChecked == true,
            LlamaCppPort = ReadPort(LlamaCppPortTextBox.Text, "llama.cpp port"),
            LlamaCppExecutablePath = ExecutablePathTextBox.Text.Trim(),
            LlamaCppModelPath = ModelPathTextBox.Text.Trim(),
            LlamaCppGpuLayers = ReadGpuLayers(GpuLayersTextBox.Text)
        };

        if (settings.AndroidPairingEnabled)
        {
            if (!IPAddress.TryParse(settings.MobileBindAddress, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork
                || !IsPrivateIpv4(address))
            {
                throw new ArgumentException("Enter a private Wi-Fi IPv4 address assigned to this PC.");
            }

            if (!IsAssignedAddress(address))
            {
                throw new ArgumentException("The bind address is not currently assigned to an active network adapter on this PC.");
            }
        }

        if (settings.LlamaCppPort == 5080 || settings.LlamaCppPort == settings.MobilePort)
        {
            throw new ArgumentException("The llama.cpp port must differ from the Baymax API and mobile HTTPS ports.");
        }

        if (settings.AndroidPairingEnabled && settings.MobilePort == 5080)
        {
            throw new ArgumentException("The mobile HTTPS port must differ from the Baymax API port 5080.");
        }

        if (settings.LlamaCppEnabled)
        {
            if (string.IsNullOrWhiteSpace(settings.LlamaCppExecutablePath)
                || !File.Exists(settings.LlamaCppExecutablePath))
            {
                throw new FileNotFoundException("Select an existing llama-server executable.");
            }

            if (string.IsNullOrWhiteSpace(settings.LlamaCppModelPath)
                || !File.Exists(settings.LlamaCppModelPath))
            {
                throw new FileNotFoundException("Select an existing GGUF model file.");
            }
        }

        return settings;
    }

    private static string ReadWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Choose an existing workspace folder using the folder picker.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The selected workspace folder does not exist: {fullPath}");
        }

        var root = Path.GetPathRoot(fullPath);
        if (root is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose a folder below the drive root, not the drive root itself.");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static int ReadPort(string text, string fieldName)
    {
        if (!int.TryParse(text, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"{fieldName} must be a number from 1 to 65535.");
        }

        return port;
    }

    private static int? ReadGpuLayers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, out var layers) || layers is < 0 or > 999)
        {
            throw new ArgumentException("GPU layers must be blank or a number from 0 to 999.");
        }

        return layers;
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool IsAssignedAddress(IPAddress address) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));

    private void UpdateServiceFields()
    {
        AndroidPairingFields.IsEnabled = AndroidPairingToggleButton.IsChecked == true && !_busy;
        MobileDirectExecutionCheckBox.IsEnabled = AndroidPairingToggleButton.IsChecked == true && !_busy;
        LlamaCppFields.IsEnabled = LlamaCppToggleButton.IsChecked == true && !_busy;
        WorkspacePathTextBox.IsEnabled = !_busy;
        BrowseWorkspaceButton.IsEnabled = !_busy;
        ReadPermissionCheckBox.IsEnabled = !_busy;
        WritePermissionCheckBox.IsEnabled = !_busy;
        ExecutePermissionCheckBox.IsEnabled = !_busy;
        AndroidPairingToggleButton.IsEnabled = !_busy;
        LlamaCppToggleButton.IsEnabled = !_busy;
    }

    private void UpdateHostButtons()
    {
        var running = _processManager.IsRunning;
        ApplyStartButton.Content = running ? "Save & Restart Baymax" : "Save & Start Baymax";
        StopBaymaxButton.IsEnabled = running && !_busy;
        ApplyStartButton.IsEnabled = !_busy;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        SettingsStatusText.Text = status;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
        UpdateServiceFields();
        UpdateHostButtons();
    }

    private void ShowSettingsError(string message)
    {
        SettingsStatusText.Text = message;
        MessageBox.Show(this, message, "Baymax settings", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
