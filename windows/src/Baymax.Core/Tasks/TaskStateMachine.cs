using Baymax.Contracts;

namespace Baymax.Core.Tasks;

public static class TaskStateMachine
{
    private static readonly IReadOnlyDictionary<TaskState, TaskState[]> AllowedTransitions =
        new Dictionary<TaskState, TaskState[]>
        {
            [TaskState.Received] =
            [
                TaskState.Understanding, TaskState.Queued, TaskState.Cancelled,
                TaskState.Failed, TaskState.Denied
            ],
            [TaskState.Understanding] =
            [
                TaskState.Planning, TaskState.Cancelled, TaskState.Failed
            ],
            [TaskState.Planning] =
            [
                TaskState.Validating, TaskState.Cancelled, TaskState.Failed
            ],
            [TaskState.Validating] =
            [
                TaskState.WaitingApproval, TaskState.Queued, TaskState.Cancelled,
                TaskState.Failed, TaskState.Denied
            ],
            [TaskState.WaitingApproval] =
            [
                TaskState.Queued, TaskState.Cancelled, TaskState.Denied
            ],
            [TaskState.Queued] =
            [
                TaskState.Running, TaskState.Cancelled, TaskState.Failed
            ],
            [TaskState.Running] =
            [
                TaskState.Verifying, TaskState.Completed, TaskState.Partial,
                TaskState.Cancelled, TaskState.Failed
            ],
            [TaskState.Verifying] =
            [
                TaskState.Completed, TaskState.Partial, TaskState.Cancelled,
                TaskState.Failed
            ]
        };

    public static bool CanTransition(TaskState current, TaskState next) =>
        AllowedTransitions.TryGetValue(current, out var allowed) && allowed.Contains(next);

    public static bool IsTerminal(TaskState state) =>
        state is TaskState.Completed or TaskState.Partial or TaskState.Failed
            or TaskState.Cancelled or TaskState.Denied;
}
