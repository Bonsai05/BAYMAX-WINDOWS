using System.Diagnostics;
using System.Net.Http;
using Baymax.Contracts;
using Baymax.Core.Models;

namespace Baymax.AgentHost;

public sealed class LlamaCppSupervisor : BackgroundService
{
    private readonly LlamaCppOptions _options;
    private readonly ILogger<LlamaCppSupervisor> _logger;
    private readonly HttpClient _healthClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };
    private ModelRuntimeStatus _status;

    public LlamaCppSupervisor(LlamaCppOptions options, ILogger<LlamaCppSupervisor> logger)
    {
        _options = options;
        _logger = logger;
        _status = new ModelRuntimeStatus(
            options.Enabled ? "starting" : "disabled",
            options.Enabled
                ? options.ManageProcess
                    ? "Waiting for the local model process to start."
                    : "Waiting for the externally managed local model server."
                : "Local model runtime is not configured.",
            null,
            0,
            options.Endpoint.ToString());
    }

    public ModelRuntimeStatus Status => Volatile.Read(ref _status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (!_options.ManageProcess)
        {
            await MonitorExternalServerAsync(stoppingToken);
            return;
        }

        await _options.VerifyArtifactsAsync(stoppingToken);
        var restartCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var process = StartServer();
            SetStatus("starting", "Waiting for llama.cpp health check.", process.Id, restartCount);
            try
            {
                await WaitUntilReadyAsync(process, stoppingToken);
                SetStatus("healthy", "llama.cpp is ready.", process.Id, restartCount);
                _logger.LogInformation(
                    "llama.cpp server is ready at {Endpoint} (process {ProcessId}).",
                    _options.Endpoint,
                    process.Id);

                await process.WaitForExitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (TimeoutException exception)
            {
                _logger.LogWarning(exception, "llama.cpp did not become ready.");
            }
            finally
            {
                StopProcess(process);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (restartCount >= _options.MaxRestarts)
            {
                SetStatus("failed", "llama.cpp exited or failed to start; restart limit reached.", null, restartCount);
                _logger.LogError("llama.cpp restart limit reached; automatic restarts are disabled.");
                return;
            }

            restartCount++;
            var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(restartCount, 5)));
            SetStatus("restarting", $"Restarting llama.cpp after {delay.TotalSeconds:0} seconds.", null, restartCount);
            _logger.LogWarning(
                "Restarting llama.cpp in {DelaySeconds} seconds (restart {RestartCount} of {MaxRestarts}).",
                delay.TotalSeconds,
                restartCount,
                _options.MaxRestarts);
            await Task.Delay(delay, stoppingToken);
        }
    }

    public override void Dispose()
    {
        _healthClient.Dispose();
        base.Dispose();
    }

    private Process StartServer()
    {
        var executablePath = Path.GetFullPath(_options.ExecutablePath
            ?? throw new InvalidOperationException("llama.cpp executable path is not configured."));
        var modelPath = Path.GetFullPath(_options.ModelPath
            ?? throw new InvalidOperationException("llama.cpp model path is not configured."));
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
                ?? throw new InvalidOperationException("The llama.cpp executable must have a parent directory."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(_options.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_options.GpuLayers is { } gpuLayers)
        {
            startInfo.ArgumentList.Add("--n-gpu-layers");
            startInfo.ArgumentList.Add(gpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                _logger.LogInformation("llama.cpp: {Output}", eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                _logger.LogWarning("llama.cpp: {Output}", eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The llama.cpp server process could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task WaitUntilReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new TimeoutException(
                    $"llama.cpp exited with code {process.ExitCode} before becoming ready.");
            }

            if (await IsHealthyAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"llama.cpp did not report healthy within {_options.StartupTimeout.TotalSeconds:0} seconds.");
    }

    private async Task MonitorExternalServerAsync(CancellationToken cancellationToken)
    {
        var startupDeadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
        var wasHealthy = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var isHealthy = await IsHealthyAsync(cancellationToken);
            if (isHealthy)
            {
                if (!wasHealthy)
                {
                    _logger.LogInformation(
                        "Connected to externally managed llama.cpp server at {Endpoint}.",
                        _options.Endpoint);
                }

                SetStatus("healthy", "Connected to the externally managed llama.cpp server.", null, 0);
                wasHealthy = true;
            }
            else
            {
                if (wasHealthy)
                {
                    _logger.LogWarning(
                        "The externally managed llama.cpp server at {Endpoint} is unavailable.",
                        _options.Endpoint);
                }

                var status = DateTimeOffset.UtcNow < startupDeadline ? "starting" : "unavailable";
                SetStatus(
                    status,
                    status == "starting"
                        ? "Waiting for the externally managed llama.cpp server."
                        : "The externally managed llama.cpp server is unavailable; start it and Baymax will reconnect.",
                    null,
                    0);
                wasHealthy = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _healthClient.GetAsync(
                new Uri(_options.Endpoint, "health"),
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void StopProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        _logger.LogInformation("Stopping llama.cpp process {ProcessId}.", process.Id);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return;
        }

        process.WaitForExit();
    }

    private void SetStatus(string status, string message, int? processId, int restartCount)
    {
        Volatile.Write(
            ref _status,
            new ModelRuntimeStatus(status, message, processId, restartCount, _options.Endpoint.ToString()));
    }
}
