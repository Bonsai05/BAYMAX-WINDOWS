using System.Collections.Concurrent;
using Baymax.Contracts;

namespace Baymax.Core.Tasks;

public sealed class TaskEngine
{
    private readonly TimeSpan _simulationDuration;
    private readonly TaskStore _store;
    private readonly ConcurrentDictionary<Guid, RunningSimulation> _runningTasks = new();

    public TaskEngine(TaskStore store, TimeSpan simulationDuration)
    {
        _store = store;
        _simulationDuration = simulationDuration;
    }

    public Task<TaskResponse> SubmitAsync(
        SubmitTaskRequest request,
        CancellationToken cancellationToken = default) =>
        _store.SubmitAsync(request, cancellationToken);

    public Task<TaskResponse?> GetAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        _store.GetAsync(taskId, cancellationToken);

    public Task<IReadOnlyList<TaskResponse>> GetRecentMobileTasksAsync(
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        _store.GetRecentMobileTasksAsync(maximumCount, cancellationToken);

    public async Task<TaskResponse> CancelAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _store.CancelAsync(taskId, cancellationToken);
        if (_runningTasks.TryGetValue(taskId, out var runningTask))
        {
            runningTask.Cancel();
        }

        return task;
    }

    public Task RecoverInterruptedTasksAsync(CancellationToken cancellationToken = default) =>
        _store.RecoverInterruptedTasksAsync(cancellationToken);

    public async Task<bool> RunNextTaskAsync(
        Func<TaskResponse, CancellationToken, Task>? processMobileTask,
        CancellationToken stoppingToken)
    {
        var task = await _store.TryStartNextQueuedTaskAsync(stoppingToken);
        if (task is null)
        {
            return false;
        }

        var taskCancellation = new RunningSimulation(stoppingToken);
        _runningTasks[task.TaskId] = taskCancellation;
        try
        {
            var current = await _store.GetAsync(task.TaskId, stoppingToken);
            if (current?.Status == TaskState.Running)
            {
                if (current.OriginDeviceId is not null && processMobileTask is not null)
                {
                    await processMobileTask(current, taskCancellation.Token);
                }
                else
                {
                    await Task.Delay(_simulationDuration, taskCancellation.Token);
                    await _store.CompleteSimulationAsync(task.TaskId, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (taskCancellation.Token.IsCancellationRequested)
        {
            // User cancellation is already persisted; shutdown leaves RUNNING for startup recovery.
        }
        finally
        {
            _runningTasks.TryRemove(task.TaskId, out _);
            taskCancellation.Dispose();
        }

        return true;
    }

    public Task RecordMobileTaskResultAsync(
        Guid taskId,
        TaskState finalState,
        string eventType,
        object result,
        CancellationToken cancellationToken = default) =>
        _store.RecordMobileTaskResultAsync(
            taskId,
            finalState,
            eventType,
            result,
            cancellationToken);

    private sealed class RunningSimulation : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public RunningSimulation(CancellationToken stoppingToken)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Token = _source.Token;
        }

        public CancellationToken Token { get; }

        public void Cancel()
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _source.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _source.Dispose();
            }
        }
    }
}
