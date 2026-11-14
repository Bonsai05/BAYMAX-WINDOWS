using System.Security.Cryptography;
using System.Text;
using Baymax.Contracts;
using Microsoft.Data.Sqlite;

namespace Baymax.Core.Persistence;

public sealed class DevicePairingStore(BaymaxDatabase database)
{
    public async Task CreateInvitationAsync(
        Guid invitationId,
        string secretHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO mobile_pairing_invitations
                (invitation_id, secret_hash, created_at, expires_at, state)
            VALUES
                ($invitationId, $secretHash, $createdAt, $expiresAt, 'CREATED');
            """;
        command.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
        command.Parameters.AddWithValue("$secretHash", secretHash);
        command.Parameters.AddWithValue("$createdAt", Format(createdAt));
        command.Parameters.AddWithValue("$expiresAt", Format(expiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            "MOBILE_PAIRING_INVITATION_CREATED",
            new { invitationId, expiresAt },
            createdAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TryCreateRequestAsync(
        Guid invitationId,
        string secretHash,
        Guid requestId,
        string deviceName,
        string publicKey,
        string publicKeySha256,
        string pollingSecretHash,
        DateTimeOffset now,
        DateTimeOffset requestExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? storedSecretHash;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = """
                SELECT secret_hash
                FROM mobile_pairing_invitations
                WHERE invitation_id = $invitationId
                  AND state = 'CREATED'
                  AND expires_at > $now;
                """;
            read.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
            read.Parameters.AddWithValue("$now", Format(now));
            storedSecretHash = (string?)await read.ExecuteScalarAsync(cancellationToken);
        }

        if (storedSecretHash is null || !FixedTimeHashEquals(storedSecretHash, secretHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var updateInvitation = connection.CreateCommand())
        {
            updateInvitation.Transaction = (SqliteTransaction)transaction;
            updateInvitation.CommandText = """
                UPDATE mobile_pairing_invitations
                SET state = 'PENDING', request_id = $requestId
                WHERE invitation_id = $invitationId
                  AND state = 'CREATED'
                  AND expires_at > $now;
                """;
            updateInvitation.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            updateInvitation.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
            updateInvitation.Parameters.AddWithValue("$now", Format(now));
            if (await updateInvitation.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var insertRequest = connection.CreateCommand())
        {
            insertRequest.Transaction = (SqliteTransaction)transaction;
            insertRequest.CommandText = """
                INSERT INTO mobile_pairing_requests
                    (request_id, invitation_id, device_name, public_key, public_key_sha256,
                     polling_secret_hash, status, created_at, expires_at)
                VALUES
                    ($requestId, $invitationId, $deviceName, $publicKey, $publicKeySha256,
                     $pollingSecretHash, 'PENDING', $createdAt, $expiresAt);
                """;
            insertRequest.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            insertRequest.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
            insertRequest.Parameters.AddWithValue("$deviceName", deviceName);
            insertRequest.Parameters.AddWithValue("$publicKey", publicKey);
            insertRequest.Parameters.AddWithValue("$publicKeySha256", publicKeySha256);
            insertRequest.Parameters.AddWithValue("$pollingSecretHash", pollingSecretHash);
            insertRequest.Parameters.AddWithValue("$createdAt", Format(now));
            insertRequest.Parameters.AddWithValue("$expiresAt", Format(requestExpiresAt));
            await insertRequest.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            "MOBILE_PAIRING_REQUESTED",
            new { requestId, invitationId, deviceName, publicKeySha256 },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<PairingRequestStatusResponse?> GetRequestStatusAsync(
        Guid requestId,
        string pollingSecretHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT polling_secret_hash, status, device_id, expires_at
            FROM mobile_pairing_requests
            WHERE request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedHash = reader.GetString(0);
        if (!FixedTimeHashEquals(storedHash, pollingSecretHash))
        {
            return null;
        }

        var status = reader.GetString(1);
        var deviceId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var expiresAt = Parse(reader.GetString(3));
        if (status == "PENDING" && expiresAt <= now)
        {
            status = "EXPIRED";
        }

        return new PairingRequestStatusResponse(status, deviceId, expiresAt);
    }

    public async Task<IReadOnlyList<PendingPairingRequestResponse>> GetPendingRequestsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var requests = new List<PendingPairingRequestResponse>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id, device_name, public_key_sha256, created_at, expires_at
            FROM mobile_pairing_requests
            WHERE status = 'PENDING' AND expires_at > $now
            ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new PendingPairingRequestResponse(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                Parse(reader.GetString(3)),
                Parse(reader.GetString(4))));
        }

        return requests;
    }

    public async Task<string?> ConfirmRequestAsync(
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string deviceName;
        string publicKey;
        string publicKeySha256;
        string invitationId;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = """
                SELECT device_name, public_key, public_key_sha256, invitation_id
                FROM mobile_pairing_requests
                WHERE request_id = $requestId
                  AND status = 'PENDING'
                  AND expires_at > $now;
                """;
            read.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            read.Parameters.AddWithValue("$now", Format(now));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            deviceName = reader.GetString(0);
            publicKey = reader.GetString(1);
            publicKeySha256 = reader.GetString(2);
            invitationId = reader.GetString(3);
        }

        var deviceId = Guid.NewGuid().ToString("D");
        await using (var insertDevice = connection.CreateCommand())
        {
            insertDevice.Transaction = (SqliteTransaction)transaction;
            insertDevice.CommandText = """
                INSERT INTO mobile_paired_devices
                    (device_id, display_name, public_key, public_key_sha256, created_at)
                VALUES
                    ($deviceId, $displayName, $publicKey, $publicKeySha256, $createdAt);
                """;
            insertDevice.Parameters.AddWithValue("$deviceId", deviceId);
            insertDevice.Parameters.AddWithValue("$displayName", deviceName);
            insertDevice.Parameters.AddWithValue("$publicKey", publicKey);
            insertDevice.Parameters.AddWithValue("$publicKeySha256", publicKeySha256);
            insertDevice.Parameters.AddWithValue("$createdAt", Format(now));
            await insertDevice.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateRequest = connection.CreateCommand())
        {
            updateRequest.Transaction = (SqliteTransaction)transaction;
            updateRequest.CommandText = """
                UPDATE mobile_pairing_requests
                SET status = 'PAIRED', device_id = $deviceId
                WHERE request_id = $requestId AND status = 'PENDING';
                """;
            updateRequest.Parameters.AddWithValue("$deviceId", deviceId);
            updateRequest.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            if (await updateRequest.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The pairing request changed while it was being confirmed.");
            }
        }

        await using (var updateInvitation = connection.CreateCommand())
        {
            updateInvitation.Transaction = (SqliteTransaction)transaction;
            updateInvitation.CommandText = """
                UPDATE mobile_pairing_invitations
                SET state = 'COMPLETED'
                WHERE invitation_id = $invitationId AND state = 'PENDING';
                """;
            updateInvitation.Parameters.AddWithValue("$invitationId", invitationId);
            await updateInvitation.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            "MOBILE_DEVICE_PAIRED",
            new { requestId, deviceId, deviceName, publicKeySha256 },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deviceId;
    }

    public async Task<bool> RejectRequestAsync(
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE mobile_pairing_requests
            SET status = 'REJECTED'
            WHERE request_id = $requestId AND status = 'PENDING' AND expires_at > $now;
            """;
        command.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("$now", Format(now));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (changed)
        {
            await using var updateInvitation = connection.CreateCommand();
            updateInvitation.Transaction = (SqliteTransaction)transaction;
            updateInvitation.CommandText = """
                UPDATE mobile_pairing_invitations
                SET state = 'CANCELLED'
                WHERE request_id = $requestId AND state = 'PENDING';
                """;
            updateInvitation.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            await updateInvitation.ExecuteNonQueryAsync(cancellationToken);
            await AddAuditAsync(
                connection,
                (SqliteTransaction)transaction,
                "MOBILE_PAIRING_REJECTED",
                new { requestId },
                now,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<bool> CancelInvitationAsync(
        Guid invitationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var updateRequest = connection.CreateCommand())
        {
            updateRequest.Transaction = (SqliteTransaction)transaction;
            updateRequest.CommandText = """
                UPDATE mobile_pairing_requests
                SET status = 'REJECTED'
                WHERE invitation_id = $invitationId AND status = 'PENDING';
                """;
            updateRequest.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
            await updateRequest.ExecuteNonQueryAsync(cancellationToken);
        }

        int changed;
        await using (var updateInvitation = connection.CreateCommand())
        {
            updateInvitation.Transaction = (SqliteTransaction)transaction;
            updateInvitation.CommandText = """
                UPDATE mobile_pairing_invitations
                SET state = 'CANCELLED'
                WHERE invitation_id = $invitationId
                  AND state IN ('CREATED', 'PENDING');
                """;
            updateInvitation.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
            changed = await updateInvitation.ExecuteNonQueryAsync(cancellationToken);
        }

        if (changed == 1)
        {
            await AddAuditAsync(
                connection,
                (SqliteTransaction)transaction,
                "MOBILE_PAIRING_INVITATION_CANCELLED",
                new { invitationId },
                now,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    public async Task<IReadOnlyList<PairedDeviceResponse>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = new List<PairedDeviceResponse>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, display_name, public_key_sha256, created_at, last_used_at, revoked_at
            FROM mobile_paired_devices
            ORDER BY created_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new PairedDeviceResponse(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5))));
        }

        return devices;
    }

    public async Task<string?> GetActiveDevicePublicKeyAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT public_key
            FROM mobile_paired_devices
            WHERE device_id = $deviceId AND revoked_at IS NULL;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<bool> TryConsumeRequestNonceAsync(
        Guid deviceId,
        string nonce,
        string method,
        string path,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = (SqliteTransaction)transaction;
            cleanup.CommandText = "DELETE FROM mobile_request_nonces WHERE expires_at <= $now;";
            cleanup.Parameters.AddWithValue("$now", Format(now));
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateDevice = connection.CreateCommand())
        {
            updateDevice.Transaction = (SqliteTransaction)transaction;
            updateDevice.CommandText = """
                UPDATE mobile_paired_devices
                SET last_used_at = $now
                WHERE device_id = $deviceId AND revoked_at IS NULL;
                """;
            updateDevice.Parameters.AddWithValue("$now", Format(now));
            updateDevice.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
            if (await updateDevice.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var insertNonce = connection.CreateCommand())
        {
            insertNonce.Transaction = (SqliteTransaction)transaction;
            insertNonce.CommandText = """
                INSERT INTO mobile_request_nonces (device_id, nonce, expires_at)
                VALUES ($deviceId, $nonce, $expiresAt);
                """;
            insertNonce.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
            insertNonce.Parameters.AddWithValue("$nonce", nonce);
            insertNonce.Parameters.AddWithValue("$expiresAt", Format(expiresAt));
            try
            {
                await insertNonce.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            "MOBILE_DEVICE_REQUEST_AUTHENTICATED",
            new { deviceId, method, path },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeDeviceAsync(
        Guid deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? publicKeySha256;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = (SqliteTransaction)transaction;
            query.CommandText = """
                SELECT public_key_sha256 FROM mobile_paired_devices
                WHERE device_id = $deviceId AND revoked_at IS NULL;
                """;
            query.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
            publicKeySha256 = (string?)await query.ExecuteScalarAsync(cancellationToken);
        }

        if (publicKeySha256 is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE mobile_paired_devices SET revoked_at = $now
                WHERE device_id = $deviceId AND revoked_at IS NULL;
                """;
            update.Parameters.AddWithValue("$now", Format(now));
            update.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddAuditAsync(
            connection,
            (SqliteTransaction)transaction,
            "MOBILE_DEVICE_REVOKED",
            new { deviceId, publicKeySha256 },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task AddAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventType,
        object details,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_audit (audit_id, event_type, plan_hash, details_json, occurred_at)
            VALUES ($auditId, $eventType, NULL, $detailsJson, $occurredAt);
            """;
        command.Parameters.AddWithValue("$auditId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$detailsJson", System.Text.Json.JsonSerializer.Serialize(details));
        command.Parameters.AddWithValue("$occurredAt", Format(occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static bool FixedTimeHashEquals(string expectedHash, string providedHash)
    {
        try
        {
            var expected = Convert.FromHexString(expectedHash);
            var provided = Convert.FromHexString(providedHash);
            return expected.Length == provided.Length
                && CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
