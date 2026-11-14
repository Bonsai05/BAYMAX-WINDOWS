using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Baymax.Desktop;

public sealed class AgentHostProcessManager : IDisposable
{
    private const string HostAddress = "http://127.0.0.1:5080/";
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        UseDefaultCredentials = true
    })
    {
        BaseAddress = new Uri(HostAddress),
        Timeout = TimeSpan.FromSeconds(3)
    };
    private readonly ConcurrentQueue<string> _recentOutput = new();
    private Process? _process;
    private string? _controlToken;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Baymax is already running. Save changes and use Restart Baymax.");
        }

        DisposeExitedProcess();
        await EnsureHostPortIsAvailableAsync(cancellationToken);

        var projectRoot = FindProjectRoot();
        var projectPath = Path.Combine(projectRoot, "windows", "src", "Baymax.AgentHost", "Baymax.AgentHost.csproj");
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("The Baymax.AgentHost project was not found.", projectPath);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);

        await ConfigureEnvironmentAsync(startInfo, settings, token, cancellationToken);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => RecordOutput(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => RecordOutput(eventArgs.Data);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Baymax.AgentHost could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _controlToken = token;

        try
        {
            await WaitForHostAsync(process, cancellationToken);
        }
        catch
        {
            if (process.HasExited)
            {
                DisposeExitedProcess();
            }

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited)
        {
            DisposeExitedProcess();
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "desktop/shutdown");
        if (_controlToken is null
            || !request.Headers.TryAddWithoutValidation("X-Baymax-Desktop-Control", _controlToken))
        {
            throw new InvalidOperationException("Baymax shutdown control could not be authenticated.");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Baymax refused the shutdown request with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Baymax did not stop gracefully. It has not been force-terminated.");
        }

        DisposeExitedProcess();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        DisposeExitedProcess();
    }

    private async Task EnsureHostPortIsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("health", cancellationToken);
            throw new InvalidOperationException(
                $"Another service is already listening at {HostAddress}. Stop the manually started AgentHost before using Desktop-managed hosting.");
        }
        catch (HttpRequestException)
        {
        }
    }

    private async Task WaitForHostAsync(Process process, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Baymax.AgentHost exited during startup.{Environment.NewLine}{GetRecentOutput()}");
            }

            try
            {
                using var response = await _httpClient.GetAsync("health", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(300, timeout.Token);
        }

        throw new TimeoutException(
            $"Baymax.AgentHost did not become ready within 40 seconds.{Environment.NewLine}{GetRecentOutput()}");
    }

    private static async Task ConfigureEnvironmentAsync(
        ProcessStartInfo startInfo,
        DesktopSettings settings,
        string token,
        CancellationToken cancellationToken)
    {
        var environment = startInfo.Environment;
        string[] managedKeys =
        [
            "Baymax__Port",
            "Baymax__WorkspacePath",
            "Baymax__Permissions__Read",
            "Baymax__Permissions__Write",
            "Baymax__Permissions__Execute",
            "Baymax__Mobile__Enabled",
            "Baymax__Mobile__BindAddress",
            "Baymax__Mobile__Port",
            "Baymax__Mobile__AllowDirectTaskExecution",
            "Baymax__LlamaCpp__Enabled",
            "Baymax__LlamaCpp__ManageProcess",
            "Baymax__LlamaCpp__ExecutablePath",
            "Baymax__LlamaCpp__ModelPath",
            "Baymax__LlamaCpp__ExecutableSha256",
            "Baymax__LlamaCpp__ModelSha256",
            "Baymax__LlamaCpp__Port",
            "Baymax__LlamaCpp__GpuLayers",
            "Baymax__LlamaCpp__ContextSize",
            "Baymax__LlamaCpp__MaxRestarts",
            "Baymax__LlamaCpp__StartupTimeoutSeconds",
            "Baymax__DesktopControlToken"
        ];
        foreach (var key in managedKeys)
        {
            environment.Remove(key);
        }

        environment["Baymax__Port"] = "5080";
        environment["Baymax__WorkspacePath"] = Path.GetFullPath(settings.WorkspacePath);
        environment["Baymax__Permissions__Read"] = settings.ReadEnabled.ToString();
        environment["Baymax__Permissions__Write"] = settings.WriteEnabled.ToString();
        environment["Baymax__Permissions__Execute"] = settings.ExecuteEnabled.ToString();
        environment["Baymax__Mobile__Enabled"] = settings.AndroidPairingEnabled.ToString();
        environment["Baymax__Mobile__BindAddress"] = settings.MobileBindAddress.Trim();
        environment["Baymax__Mobile__Port"] = settings.MobilePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["Baymax__Mobile__AllowDirectTaskExecution"] =
            settings.MobileDirectExecutionEnabled.ToString();
        environment["Baymax__LlamaCpp__Enabled"] = settings.LlamaCppEnabled.ToString();
        environment["Baymax__LlamaCpp__ManageProcess"] = "true";
        environment["Baymax__LlamaCpp__Port"] = settings.LlamaCppPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["Baymax__DesktopControlToken"] = token;
        if (settings.LlamaCppGpuLayers is { } gpuLayers)
        {
            environment["Baymax__LlamaCpp__GpuLayers"] = gpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (settings.LlamaCppEnabled)
        {
            environment["Baymax__LlamaCpp__ExecutablePath"] = Path.GetFullPath(settings.LlamaCppExecutablePath);
            environment["Baymax__LlamaCpp__ModelPath"] = Path.GetFullPath(settings.LlamaCppModelPath);
            environment["Baymax__LlamaCpp__ExecutableSha256"] =
                await ComputeSha256Async(settings.LlamaCppExecutablePath, cancellationToken);
            environment["Baymax__LlamaCpp__ModelSha256"] =
                await ComputeSha256Async(settings.LlamaCppModelPath, cancellationToken);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "windows", "Baymax.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Baymax solution. Run Baymax Desktop from a built workspace that contains windows\\Baymax.sln.");
    }

    private void RecordOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _recentOutput.Enqueue(line);
        while (_recentOutput.Count > 40)
        {
            _recentOutput.TryDequeue(out _);
        }
    }

    private string GetRecentOutput() => string.Join(Environment.NewLine, _recentOutput);

    private void DisposeExitedProcess()
    {
        if (_process is null || !_process.HasExited)
        {
            return;
        }

        _process.Dispose();
        _process = null;
        _controlToken = null;
    }
}
