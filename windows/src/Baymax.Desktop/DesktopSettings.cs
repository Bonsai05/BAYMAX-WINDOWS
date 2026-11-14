using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Baymax.Desktop;

public sealed class DesktopSettings
{
    public string WorkspacePath { get; set; } = FindDefaultWorkspace();
    public bool ReadEnabled { get; set; } = true;
    public bool WriteEnabled { get; set; } = true;
    public bool ExecuteEnabled { get; set; } = true;
    public bool AndroidPairingEnabled { get; set; }
    public bool MobileDirectExecutionEnabled { get; set; }
    public string MobileBindAddress { get; set; } = FindDefaultBindAddress();
    public int MobilePort { get; set; } = 5081;
    public bool LlamaCppEnabled { get; set; }
    public int LlamaCppPort { get; set; } = 8081;
    public string LlamaCppExecutablePath { get; set; } = string.Empty;
    public string LlamaCppModelPath { get; set; } = string.Empty;
    public int? LlamaCppGpuLayers { get; set; } = 99;

    public static DesktopSettings CreateDefault() => new();

    private static string FindDefaultWorkspace() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "WORKENIVORNMENT");

    private static string FindDefaultBindAddress()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .OrderBy(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address));
        return addresses.FirstOrDefault()?.ToString() ?? string.Empty;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}

public static class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Baymax",
        "desktop-settings.json");

    public static DesktopSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return DesktopSettings.CreateDefault();
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new InvalidDataException("Baymax desktop settings are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Baymax desktop settings could not be read from {FilePath}.", exception);
        }
    }

    public static async Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The Baymax settings directory could not be resolved.");
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
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
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
