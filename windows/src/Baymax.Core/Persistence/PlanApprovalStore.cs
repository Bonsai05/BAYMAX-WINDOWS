using System.Security.Cryptography;
using System.Text.Json;
using Baymax.Contracts;
using Microsoft.Data.Sqlite;

namespace Baymax.Core.Persistence;

public sealed class PlanApprovalStore(BaymaxDatabase database)
{
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(5);

    public async Task<PlanApprovalResponse> CreateAsync(
        TaskPlanResponse plan,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        var planHash = GetPlanHash(plan);
        var approvalId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(ApprovalLifetime);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO plan_approvals (approval_id, plan_hash, created_at, expires_at, approved_by)
                VALUES ($approvalId, $planHash, $createdAt, $expiresAt, $approvedBy);
                """;
            command.Parameters.AddWithValue("$approvalId", approvalId.ToString("D"));
            command.Parameters.AddWithValue("$planHash", planHash);
            command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            command.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O"));
            command.Parameters.AddWithValue("$approvedBy", approvedBy);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            "APPROVAL_CREATED",
            planHash,
            new { approvalId, expiresAt, approvedBy },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PlanApprovalResponse(approvalId, planHash, expiresAt, approvedBy);
    }

    public async Task<bool> TryConsumeAsync(
        Guid approvalId,
        string planHash,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int affectedRows;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE plan_approvals
                SET consumed_at = $now
                WHERE approval_id = $approvalId
                  AND plan_hash = $planHash
                  AND approved_by = $approvedBy
                  AND expires_at > $now
                  AND revoked_at IS NULL
                  AND consumed_at IS NULL;
                """;
            command.Parameters.AddWithValue("$approvalId", approvalId.ToString("D"));
            command.Parameters.AddWithValue("$planHash", planHash);
            command.Parameters.AddWithValue("$approvedBy", approvedBy);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            affectedRows == 1 ? "APPROVAL_CONSUMED" : "APPROVAL_REJECTED",
            planHash,
            new { approvalId, approvedBy, accepted = affectedRows == 1 },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affectedRows == 1;
    }

    public async Task<bool> RevokeAsync(
        Guid approvalId,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int affectedRows;
        string? planHash = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE plan_approvals
                SET revoked_at = $now
                WHERE approval_id = $approvalId
                  AND approved_by = $approvedBy
                  AND revoked_at IS NULL
                  AND consumed_at IS NULL;
                """;
            command.Parameters.AddWithValue("$approvalId", approvalId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$approvedBy", approvedBy);
            affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (affectedRows == 1)
        {
            await using var query = connection.CreateCommand();
            query.Transaction = (SqliteTransaction)transaction;
            query.CommandText = "SELECT plan_hash FROM plan_approvals WHERE approval_id = $approvalId;";
            query.Parameters.AddWithValue("$approvalId", approvalId.ToString("D"));
            planHash = (string?)await query.ExecuteScalarAsync(cancellationToken);
        }

        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            affectedRows == 1 ? "APPROVAL_REVOKED" : "APPROVAL_REVOCATION_REJECTED",
            planHash,
            new { approvalId, approvedBy, revoked = affectedRows == 1 },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affectedRows == 1;
    }

    public async Task RecordExecutionAsync(
        string planHash,
        PlanExecutionResponse response,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var details = new
        {
            response.Status,
            actor,
            actions = response.Actions.Select(action => new
            {
                action.ActionId,
                action.Ordinal,
                action.Name,
                action.Status,
                action.FailureCode
            })
        };
        await RecordAuditAsync("EXECUTION_COMPLETED", planHash, details, cancellationToken);
    }

    public async Task RecordDeniedAsync(
        string? planHash,
        string reasonCode,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await RecordAuditAsync(
            "EXECUTION_DENIED",
            planHash,
            new { reasonCode, actor },
            cancellationToken);
    }

    private async Task RecordAuditAsync(
        string eventType,
        string? planHash,
        object details,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            eventType,
            planHash,
            details,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AddAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventType,
        string? planHash,
        object details,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_audit (audit_id, event_type, plan_hash, details_json, occurred_at)
            VALUES ($auditId, $eventType, $planHash, $detailsJson, $occurredAt);
            """;
        command.Parameters.AddWithValue("$auditId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$planHash", (object?)planHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$detailsJson", JsonSerializer.Serialize(details));
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string GetPlanHash(TaskPlanResponse plan)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("summary", plan.Summary);
            writer.WriteBoolean("needsClarification", plan.NeedsClarification);
            writer.WriteString("clarifyingQuestion", plan.ClarifyingQuestion);
            writer.WriteStartArray("actions");
            foreach (var action in plan.Actions)
            {
                writer.WriteStartObject();
                writer.WriteString("actionId", action.ActionId);
                writer.WriteNumber("ordinal", action.Ordinal);
                writer.WriteString("name", action.Name);
                writer.WritePropertyName("arguments");
                WriteCanonicalJson(writer, action.Arguments);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
