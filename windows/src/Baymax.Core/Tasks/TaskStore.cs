using System.Globalization;
using System.Text.Json;
using Baymax.Contracts;
using Baymax.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Baymax.Core.Tasks;

public sealed class IdempotencyConflictException(string message) : Exception(message);

public sealed class TaskNotFoundException(Guid taskId)
    : Exception($"Task '{taskId}' was not found.");

public sealed class TaskStateConflictException(string message) : Exception(message);

public sealed class TaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BaymaxDatabase _database;

    public TaskStore(BaymaxDatabase database)
    {
        _database = database;
    }

    public async Task<TaskResponse> SubmitAsync(
        SubmitTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var taskId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var now = UtcNow();
        var timestamp = FormatTimestamp(now);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var insertTask = connection.CreateCommand())
        {
            insertTask.Transaction = transaction;
            insertTask.CommandText = """
                INSERT INTO tasks (
                    task_id, origin_device_id, user_instruction, status,
                    created_at, updated_at, current_step, idempotency_key
                )
                VALUES (
                    $taskId, $originDeviceId, $userInstruction, 'RECEIVED',
                    $createdAt, $updatedAt, 1, $idempotencyKey
                )
                ON CONFLICT(idempotency_key) DO NOTHING;
                """;
            insertTask.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            insertTask.Parameters.AddWithValue("$originDeviceId", (object?)request.OriginDeviceId ?? DBNull.Value);
            insertTask.Parameters.AddWithValue("$userInstruction", request.UserInstruction);
            insertTask.Parameters.AddWithValue("$createdAt", timestamp);
            insertTask.Parameters.AddWithValue("$updatedAt", timestamp);
            insertTask.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
            await insertTask.ExecuteNonQueryAsync(cancellationToken);
        }

        var storedTaskId = await FindTaskIdByIdempotencyKeyAsync(
            connection,
            transaction,
            request.IdempotencyKey,
            cancellationToken);
        if (storedTaskId is null)
        {
            throw new InvalidOperationException("The task was not available after the submission was persisted.");
        }

        if (storedTaskId != taskId.ToString("D"))
        {
            var existing = await LoadTaskAsync(connection, transaction, storedTaskId, cancellationToken)
                ?? throw new InvalidOperationException("The existing idempotent task could not be loaded.");
            if (existing.UserInstruction != request.UserInstruction
                || existing.OriginDeviceId != request.OriginDeviceId)
            {
                throw new IdempotencyConflictException(
                    "This idempotency key was already used for a different task request.");
            }

            transaction.Commit();
            return existing;
        }

        await using (var insertStep = connection.CreateCommand())
        {
            insertStep.Transaction = transaction;
            insertStep.CommandText = """
                INSERT INTO task_steps (
                    step_id, task_id, ordinal, name, status,
                    idempotency_key, created_at
                )
                VALUES (
                    $stepId, $taskId, 1, $stepName, 'PENDING',
                    $idempotencyKey, $createdAt
                );
                """;
            insertStep.Parameters.AddWithValue("$stepId", stepId.ToString("D"));
            insertStep.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            insertStep.Parameters.AddWithValue(
                "$stepName",
                request.OriginDeviceId is null ? "Simulated task" : "Android workspace task");
            insertStep.Parameters.AddWithValue("$idempotencyKey", $"{request.IdempotencyKey}:simulation");
            insertStep.Parameters.AddWithValue("$createdAt", timestamp);
            await insertStep.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendEventAsync(
            connection,
            transaction,
            taskId.ToString("D"),
            "TASK_RECEIVED",
            JsonSerializer.Serialize(new { status = "RECEIVED" }, JsonOptions),
            now,
            cancellationToken);

        if (!TaskStateMachine.CanTransition(TaskState.Received, TaskState.Queued))
        {
            throw new InvalidOperationException("The task state machine does not allow a received task to be queued.");
        }

        await using (var queueTask = connection.CreateCommand())
        {
            queueTask.Transaction = transaction;
            queueTask.CommandText = """
                UPDATE tasks SET status = 'QUEUED', updated_at = $updatedAt
                WHERE task_id = $taskId AND status = 'RECEIVED';
                """;
            queueTask.Parameters.AddWithValue("$updatedAt", timestamp);
            queueTask.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            if (await queueTask.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The received task could not be queued.");
            }
        }

        await AppendEventAsync(
            connection,
            transaction,
            taskId.ToString("D"),
            "TASK_QUEUED",
            JsonSerializer.Serialize(new { status = "QUEUED" }, JsonOptions),
            now,
            cancellationToken);

        var response = await LoadTaskAsync(connection, transaction, taskId.ToString("D"), cancellationToken)
            ?? throw new InvalidOperationException("The submitted task could not be loaded after persistence.");
        transaction.Commit();
        return response;
    }

    public async Task<TaskResponse?> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadTaskAsync(connection, null, taskId.ToString("D"), cancellationToken);
    }

    public async Task<IReadOnlyList<TaskResponse>> GetRecentMobileTasksAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), "The result count must be between 1 and 100.");
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var taskIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT task_id FROM tasks
                WHERE origin_device_id IS NOT NULL
                ORDER BY created_at DESC, task_id DESC
                LIMIT $maximumCount;
                """;
            command.Parameters.AddWithValue("$maximumCount", maximumCount);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                taskIds.Add(reader.GetString(0));
            }
        }

        var tasks = new List<TaskResponse>(taskIds.Count);
        foreach (var taskId in taskIds)
        {
            var task = await LoadTaskAsync(connection, null, taskId, cancellationToken)
                ?? throw new InvalidOperationException($"Mobile task '{taskId}' disappeared while loading recent tasks.");
            tasks.Add(task);
        }

        return tasks;
    }

    public async Task<TaskResponse> CancelAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var taskIdText = taskId.ToString("D");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var existing = await LoadTaskAsync(connection, transaction, taskIdText, cancellationToken)
            ?? throw new TaskNotFoundException(taskId);
        if (existing.Status == TaskState.Cancelled)
        {
            transaction.Commit();
            return existing;
        }

        if (!TaskStateMachine.CanTransition(existing.Status, TaskState.Cancelled))
        {
            throw new TaskStateConflictException(
                $"A task in state {existing.Status} cannot be cancelled.");
        }

        var now = UtcNow();
        var timestamp = FormatTimestamp(now);
        await using (var updateTask = connection.CreateCommand())
        {
            updateTask.Transaction = transaction;
            updateTask.CommandText = """
                UPDATE tasks
                SET status = 'CANCELLED', updated_at = $updatedAt
                WHERE task_id = $taskId;
                """;
            updateTask.Parameters.AddWithValue("$updatedAt", timestamp);
            updateTask.Parameters.AddWithValue("$taskId", taskIdText);
            await updateTask.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateStep = connection.CreateCommand())
        {
            updateStep.Transaction = transaction;
            updateStep.CommandText = """
                UPDATE task_steps
                SET status = 'CANCELLED', completed_at = $completedAt
                WHERE task_id = $taskId AND status IN ('PENDING', 'RUNNING');
                """;
            updateStep.Parameters.AddWithValue("$completedAt", timestamp);
            updateStep.Parameters.AddWithValue("$taskId", taskIdText);
            await updateStep.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendEventAsync(
            connection,
            transaction,
            taskIdText,
            "TASK_CANCELLED",
            JsonSerializer.Serialize(new { status = "CANCELLED" }, JsonOptions),
            now,
            cancellationToken);

        var response = await LoadTaskAsync(connection, transaction, taskIdText, cancellationToken)
            ?? throw new InvalidOperationException("The cancelled task could not be loaded.");
        transaction.Commit();
        return response;
    }

    public async Task RecoverInterruptedTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var runningTasks = new List<(string TaskId, bool IsMobile)>();
        await using (var selectTasks = connection.CreateCommand())
        {
            selectTasks.Transaction = transaction;
            selectTasks.CommandText = "SELECT task_id, origin_device_id FROM tasks WHERE status = 'RUNNING';";
            await using var reader = await selectTasks.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                runningTasks.Add((reader.GetString(0), !reader.IsDBNull(1)));
            }
        }

        foreach (var (taskId, isMobile) in runningTasks)
        {
            var now = UtcNow();
            var timestamp = FormatTimestamp(now);
            await using (var updateTask = connection.CreateCommand())
            {
                updateTask.Transaction = transaction;
                updateTask.CommandText = isMobile
                    ? """
                      UPDATE tasks SET status = 'FAILED', updated_at = $updatedAt
                      WHERE task_id = $taskId AND status = 'RUNNING';
                      """
                    : """
                      UPDATE tasks SET status = 'QUEUED', updated_at = $updatedAt
                      WHERE task_id = $taskId AND status = 'RUNNING';
                      """;
                updateTask.Parameters.AddWithValue("$updatedAt", timestamp);
                updateTask.Parameters.AddWithValue("$taskId", taskId);
                await updateTask.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var updateStep = connection.CreateCommand())
            {
                updateStep.Transaction = transaction;
                updateStep.CommandText = isMobile
                    ? """
                      UPDATE task_steps SET status = 'FAILED', completed_at = $completedAt
                      WHERE task_id = $taskId AND status = 'RUNNING';
                      """
                    : """
                      UPDATE task_steps SET status = 'PENDING', completed_at = NULL
                      WHERE task_id = $taskId AND status = 'RUNNING';
                      """;
                if (isMobile)
                {
                    updateStep.Parameters.AddWithValue("$completedAt", timestamp);
                }

                updateStep.Parameters.AddWithValue("$taskId", taskId);
                await updateStep.ExecuteNonQueryAsync(cancellationToken);
            }

            var recoveryPayload = isMobile
                ? JsonSerializer.Serialize(
                    new
                    {
                        previousStatus = "RUNNING",
                        status = "FAILED",
                        reason = "Execution was interrupted; automatic retry was prevented because actions may have partially completed."
                    },
                    JsonOptions)
                : JsonSerializer.Serialize(
                    new { previousStatus = "RUNNING", status = "QUEUED" },
                    JsonOptions);
            await AppendEventAsync(
                connection,
                transaction,
                taskId,
                isMobile ? "TASK_INTERRUPTED" : "TASK_RECOVERED",
                recoveryPayload,
                now,
                cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<TaskResponse?> TryStartNextQueuedTaskAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        string? taskId;
        await using (var selectTask = connection.CreateCommand())
        {
            selectTask.Transaction = transaction;
            selectTask.CommandText = """
                SELECT task_id FROM tasks
                WHERE status = 'QUEUED'
                ORDER BY created_at, task_id
                LIMIT 1;
                """;
            taskId = (string?)await selectTask.ExecuteScalarAsync(cancellationToken);
        }

        if (taskId is null)
        {
            transaction.Commit();
            return null;
        }

        if (!TaskStateMachine.CanTransition(TaskState.Queued, TaskState.Running))
        {
            throw new InvalidOperationException("The task state machine does not allow a queued task to start.");
        }

        var now = UtcNow();
        var timestamp = FormatTimestamp(now);
        await using (var updateTask = connection.CreateCommand())
        {
            updateTask.Transaction = transaction;
            updateTask.CommandText = """
                UPDATE tasks SET status = 'RUNNING', updated_at = $updatedAt
                WHERE task_id = $taskId AND status = 'QUEUED';
                """;
            updateTask.Parameters.AddWithValue("$updatedAt", timestamp);
            updateTask.Parameters.AddWithValue("$taskId", taskId);
            if (await updateTask.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Commit();
                return null;
            }
        }

        await using (var updateStep = connection.CreateCommand())
        {
            updateStep.Transaction = transaction;
            updateStep.CommandText = """
                UPDATE task_steps
                SET status = 'RUNNING'
                WHERE task_id = $taskId AND status = 'PENDING';
                """;
            updateStep.Parameters.AddWithValue("$taskId", taskId);
            await updateStep.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendEventAsync(
            connection,
            transaction,
            taskId,
            "TASK_STARTED",
            JsonSerializer.Serialize(new { status = "RUNNING" }, JsonOptions),
            now,
            cancellationToken);

        var response = await LoadTaskAsync(connection, transaction, taskId, cancellationToken)
            ?? throw new InvalidOperationException("The started task could not be loaded.");
        transaction.Commit();
        return response;
    }

    public async Task CompleteSimulationAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var taskIdText = taskId.ToString("D");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var task = await LoadTaskAsync(connection, transaction, taskIdText, cancellationToken);
        if (task is null)
        {
            throw new TaskNotFoundException(taskId);
        }

        if (task.Status != TaskState.Running)
        {
            transaction.Commit();
            return;
        }

        if (!TaskStateMachine.CanTransition(TaskState.Running, TaskState.Completed))
        {
            throw new InvalidOperationException("The task state machine does not allow a running task to complete.");
        }

        var now = UtcNow();
        var timestamp = FormatTimestamp(now);
        await using (var updateTask = connection.CreateCommand())
        {
            updateTask.Transaction = transaction;
            updateTask.CommandText = """
                UPDATE tasks SET status = 'COMPLETED', updated_at = $updatedAt
                WHERE task_id = $taskId AND status = 'RUNNING';
                """;
            updateTask.Parameters.AddWithValue("$updatedAt", timestamp);
            updateTask.Parameters.AddWithValue("$taskId", taskIdText);
            await updateTask.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateStep = connection.CreateCommand())
        {
            updateStep.Transaction = transaction;
            updateStep.CommandText = """
                UPDATE task_steps
                SET status = 'COMPLETED', completed_at = $completedAt
                WHERE task_id = $taskId AND status = 'RUNNING';
                """;
            updateStep.Parameters.AddWithValue("$completedAt", timestamp);
            updateStep.Parameters.AddWithValue("$taskId", taskIdText);
            await updateStep.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendEventAsync(
            connection,
            transaction,
            taskIdText,
            "TASK_COMPLETED",
            JsonSerializer.Serialize(new { status = "COMPLETED" }, JsonOptions),
            now,
            cancellationToken);
        transaction.Commit();
    }

    public async Task RecordMobileTaskResultAsync(
        Guid taskId,
        TaskState finalState,
        string eventType,
        object result,
        CancellationToken cancellationToken = default)
    {
        if (finalState is not (TaskState.Completed or TaskState.Partial or TaskState.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(finalState), "A mobile task result must be terminal.");
        }

        if (eventType is not ("TASK_EXECUTION_RESULT" or "TASK_EXECUTION_FAILED"))
        {
            throw new ArgumentException("The mobile task event type is not supported.", nameof(eventType));
        }

        var taskIdText = taskId.ToString("D");
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var task = await LoadTaskAsync(connection, transaction, taskIdText, cancellationToken)
            ?? throw new TaskNotFoundException(taskId);
        if (task.Status != TaskState.Running
            || !TaskStateMachine.CanTransition(TaskState.Running, finalState))
        {
            throw new TaskStateConflictException(
                $"A mobile task in state {task.Status} cannot transition to {finalState}.");
        }

        var now = UtcNow();
        var timestamp = FormatTimestamp(now);
        await using (var updateTask = connection.CreateCommand())
        {
            updateTask.Transaction = transaction;
            updateTask.CommandText = """
                UPDATE tasks SET status = $status, updated_at = $updatedAt
                WHERE task_id = $taskId AND status = 'RUNNING';
                """;
            updateTask.Parameters.AddWithValue("$status", finalState.ToString().ToUpperInvariant());
            updateTask.Parameters.AddWithValue("$updatedAt", timestamp);
            updateTask.Parameters.AddWithValue("$taskId", taskIdText);
            await updateTask.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateStep = connection.CreateCommand())
        {
            updateStep.Transaction = transaction;
            updateStep.CommandText = """
                UPDATE task_steps
                SET status = $status, completed_at = $completedAt
                WHERE task_id = $taskId AND status = 'RUNNING';
                """;
            updateStep.Parameters.AddWithValue(
                "$status",
                finalState == TaskState.Completed ? "COMPLETED" : "FAILED");
            updateStep.Parameters.AddWithValue("$completedAt", timestamp);
            updateStep.Parameters.AddWithValue("$taskId", taskIdText);
            await updateStep.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendEventAsync(
            connection,
            transaction,
            taskIdText,
            eventType,
            JsonSerializer.Serialize(result, JsonOptions),
            now,
            cancellationToken);
        transaction.Commit();
    }

    private static async Task<string?> FindTaskIdByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT task_id FROM tasks WHERE idempotency_key = $idempotencyKey;";
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<TaskResponse?> LoadTaskAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string taskId,
        CancellationToken cancellationToken)
    {
        string instruction;
        string? originDeviceId;
        TaskState status;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        int currentStep;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT user_instruction, origin_device_id, status,
                       created_at, updated_at, current_step
                FROM tasks WHERE task_id = $taskId;
                """;
            command.Parameters.AddWithValue("$taskId", taskId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            instruction = reader.GetString(0);
            originDeviceId = reader.IsDBNull(1) ? null : reader.GetString(1);
            status = ParseState(reader.GetString(2));
            createdAt = ParseTimestamp(reader.GetString(3));
            updatedAt = ParseTimestamp(reader.GetString(4));
            currentStep = reader.GetInt32(5);
        }

        var steps = new List<TaskStepResponse>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT step_id, ordinal, name, status, created_at, completed_at
                FROM task_steps WHERE task_id = $taskId ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$taskId", taskId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                steps.Add(new TaskStepResponse(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ParseTimestamp(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5))));
            }
        }

        var events = new List<TaskEventResponse>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT event_id, sequence, event_type, payload_json, occurred_at
                FROM task_events WHERE task_id = $taskId ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$taskId", taskId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new TaskEventResponse(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ParseTimestamp(reader.GetString(4))));
            }
        }

        return new TaskResponse(
            Guid.Parse(taskId),
            instruction,
            originDeviceId,
            status,
            createdAt,
            updatedAt,
            currentStep,
            steps,
            events);
    }

    private static async Task AppendEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string eventType,
        string payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO task_events (event_id, task_id, sequence, event_type, payload_json, occurred_at)
            SELECT $eventId, $taskId, COALESCE(MAX(sequence), 0) + 1,
                   $eventType, $payloadJson, $occurredAt
            FROM task_events WHERE task_id = $taskId;
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$occurredAt", FormatTimestamp(occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TaskState ParseState(string value) =>
        Enum.Parse<TaskState>(value, ignoreCase: true);

    private static DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
