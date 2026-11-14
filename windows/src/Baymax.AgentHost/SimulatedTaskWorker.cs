using System.Net.Http;
using System.Text.Json;
using Baymax.Contracts;
using Baymax.Core.Models;
using Baymax.Core.Planning;
using Baymax.Core.Tasks;

namespace Baymax.AgentHost;

public sealed record MobileTaskExecutionOptions(bool Enabled);

public sealed class SimulatedTaskWorker(
    TaskEngine taskEngine,
    TaskPlanner planner,
    ExecutionPolicy executionPolicy,
    FileSystemActionExecutor executor,
    MobileTaskExecutionOptions mobileOptions,
    ILogger<SimulatedTaskWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await taskEngine.RunNextTaskAsync(ProcessMobileTaskAsync, stoppingToken))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
            else
            {
                logger.LogInformation("Finished processing a queued task.");
            }
        }
    }

    private async Task ProcessMobileTaskAsync(TaskResponse task, CancellationToken cancellationToken)
    {
        if (!mobileOptions.Enabled || !executionPolicy.ExecuteEnabled)
        {
            await RecordFailureAsync(
                task.TaskId,
                "EXECUTION_DISABLED",
                "Direct Android task execution is disabled in Baymax Settings.",
                cancellationToken);
            return;
        }

        try
        {
            var plan = await planner.CreateMobilePlanAsync(
                new CreatePlanRequest(task.UserInstruction),
                cancellationToken);
            if (plan.NeedsClarification)
            {
                await RecordFailureAsync(
                    task.TaskId,
                    "NEEDS_CLARIFICATION",
                    plan.ClarifyingQuestion,
                    cancellationToken);
                return;
            }

            planner.ValidateMobilePlanForExecution(plan);
            var execution = await executor.ExecuteAsync(plan, cancellationToken);
            var finalState = execution.Status switch
            {
                "COMPLETED" => TaskState.Completed,
                "PARTIAL" => TaskState.Partial,
                _ => TaskState.Failed
            };
            await taskEngine.RecordMobileTaskResultAsync(
                task.TaskId,
                finalState,
                "TASK_EXECUTION_RESULT",
                new
                {
                    summary = plan.Summary,
                    actions = plan.Actions.Select(action => new
                    {
                        action.Ordinal,
                        action.Name,
                        arguments = action.Arguments
                    }),
                    execution
                },
                cancellationToken);
            logger.LogInformation(
                "Mobile task {TaskId} finished with status {Status}.",
                task.TaskId,
                execution.Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ModelUnavailableException
                                            or HttpRequestException
                                            or InvalidDataException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                            or JsonException
                                            or TimeoutException)
        {
            logger.LogError(exception, "Mobile task {TaskId} failed before or during planning.", task.TaskId);
            await RecordFailureAsync(
                task.TaskId,
                "TASK_PROCESSING_FAILED",
                exception.Message,
                cancellationToken);
        }
    }

    private Task RecordFailureAsync(
        Guid taskId,
        string code,
        string message,
        CancellationToken cancellationToken) =>
        taskEngine.RecordMobileTaskResultAsync(
            taskId,
            TaskState.Failed,
            "TASK_EXECUTION_FAILED",
            new { status = "FAILED", code, message },
            cancellationToken);
}
