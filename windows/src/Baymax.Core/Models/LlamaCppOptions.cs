using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Baymax.Core.Models;

public sealed class LlamaCppOptions
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public LlamaCppOptions(
        bool enabled,
        string? executablePath,
        string? modelPath,
        string? executableSha256,
        string? modelSha256,
        int port = 8081,
        int contextSize = 8192,
        int? gpuLayers = null,
        int maxRestarts = 3,
        TimeSpan? startupTimeout = null,
        bool manageProcess = true)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be from 1 to 65535.");
        }

        if (contextSize is < 512 or > 131072)
        {
            throw new ArgumentOutOfRangeException(nameof(contextSize), "Context size must be from 512 to 131072.");
        }

        if (gpuLayers is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(gpuLayers), "GPU layers must be from 0 to 999.");
        }

        if (maxRestarts is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRestarts), "Maximum restarts must be from 0 to 10.");
        }

        var actualStartupTimeout = startupTimeout ?? TimeSpan.FromMinutes(5);
        if (actualStartupTimeout < TimeSpan.FromSeconds(10) || actualStartupTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(startupTimeout),
                "Startup timeout must be from 10 seconds to 30 minutes.");
        }

        if (enabled && manageProcess)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
            ValidateHash(executableSha256, nameof(executableSha256));
            ValidateHash(modelSha256, nameof(modelSha256));
        }

        Enabled = enabled;
        ExecutablePath = executablePath;
        ModelPath = modelPath;
        ExecutableSha256 = executableSha256;
        ModelSha256 = modelSha256;
        Port = port;
        ContextSize = contextSize;
        GpuLayers = gpuLayers;
        MaxRestarts = maxRestarts;
        StartupTimeout = actualStartupTimeout;
        ManageProcess = manageProcess;
    }

    public bool Enabled { get; }
    public string? ExecutablePath { get; }
    public string? ModelPath { get; }
    public string? ExecutableSha256 { get; }
    public string? ModelSha256 { get; }
    public int Port { get; }
    public int ContextSize { get; }
    public int? GpuLayers { get; }
    public int MaxRestarts { get; }
    public TimeSpan StartupTimeout { get; }
    public bool ManageProcess { get; }
    public Uri Endpoint => new($"http://127.0.0.1:{Port}/");

    public async Task VerifyArtifactsAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || !ManageProcess || ExecutablePath is null || ModelPath is null
            || ExecutableSha256 is null || ModelSha256 is null)
        {
            throw new InvalidOperationException(
                "llama.cpp artifact verification requires enabled, managed, complete configuration.");
        }

        await VerifyFileAsync(ExecutablePath, ExecutableSha256, "llama-server", cancellationToken);
        await VerifyFileAsync(ModelPath, ModelSha256, "model", cancellationToken);
    }

    private static async Task VerifyFileAsync(
        string configuredPath,
        string expectedHash,
        string artifactName,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(configuredPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configured llama.cpp {artifactName} file was not found.", path);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configured llama.cpp {artifactName} SHA-256 did not match the expected value.");
        }
    }

    private static void ValidateHash(string? value, string key)
    {
        if (value is null || !Sha256Pattern.IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Baymax:LlamaCpp:{key} must contain exactly 64 hexadecimal characters.");
        }
    }
}
