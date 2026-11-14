using Microsoft.Data.Sqlite;

namespace Baymax.Core.Persistence;

public sealed class BaymaxDatabase
{
    private const int CurrentSchemaVersion = 6;
    private readonly string _connectionString;

    public BaymaxDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public string DatabasePath { get; }

    internal string ConnectionString => _connectionString;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("The database path must have a parent directory.");
        Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var foreignKeysCommand = connection.CreateCommand())
        {
            foreignKeysCommand.CommandText = "PRAGMA foreign_keys = ON;";
            await foreignKeysCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var migrationTableCommand = connection.CreateCommand())
        {
            migrationTableCommand.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                """;
            await migrationTableCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var currentVersion = await GetCurrentVersionAsync(connection, cancellationToken);
        if (currentVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {currentVersion} is newer than this application supports ({CurrentSchemaVersion}).");
        }

        if (currentVersion == 0)
        {
            using var transaction = connection.BeginTransaction();
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = """
                CREATE TABLE tasks (
                    task_id TEXT PRIMARY KEY NOT NULL,
                    origin_device_id TEXT,
                    user_instruction TEXT NOT NULL,
                    status TEXT NOT NULL CHECK (status IN (
                        'RECEIVED', 'UNDERSTANDING', 'PLANNING', 'VALIDATING',
                        'WAITING_APPROVAL', 'QUEUED', 'RUNNING', 'VERIFYING',
                        'COMPLETED', 'PARTIAL', 'FAILED', 'CANCELLED', 'DENIED'
                    )),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    plan_version INTEGER NOT NULL DEFAULT 0,
                    plan_hash TEXT,
                    current_step INTEGER NOT NULL DEFAULT 0,
                    idempotency_key TEXT NOT NULL UNIQUE
                );

                CREATE TABLE task_events (
                    event_id TEXT PRIMARY KEY NOT NULL,
                    task_id TEXT NOT NULL REFERENCES tasks(task_id),
                    sequence INTEGER NOT NULL,
                    event_type TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    UNIQUE (task_id, sequence)
                );

                CREATE INDEX idx_task_events_task_sequence
                    ON task_events(task_id, sequence);

                INSERT INTO schema_migrations (version, applied_at)
                VALUES (1, $appliedAt);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$appliedAt",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            currentVersion = 1;
        }

        if (currentVersion == 1)
        {
            using var transaction = connection.BeginTransaction();
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = """
                CREATE TABLE task_steps (
                    step_id TEXT PRIMARY KEY NOT NULL,
                    task_id TEXT NOT NULL REFERENCES tasks(task_id),
                    ordinal INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    status TEXT NOT NULL CHECK (status IN (
                        'PENDING', 'RUNNING', 'COMPLETED', 'FAILED', 'CANCELLED'
                    )),
                    idempotency_key TEXT NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    completed_at TEXT,
                    UNIQUE (task_id, ordinal)
                );

                CREATE INDEX idx_task_steps_task_ordinal
                    ON task_steps(task_id, ordinal);

                INSERT INTO schema_migrations (version, applied_at)
                VALUES (2, $appliedAt);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$appliedAt",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            currentVersion = 2;
        }

        if (currentVersion == 2)
        {
            using var transaction = connection.BeginTransaction();
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = """
                CREATE TABLE plan_approvals (
                    approval_id TEXT PRIMARY KEY NOT NULL,
                    plan_hash TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    revoked_at TEXT,
                    consumed_at TEXT
                );

                CREATE INDEX idx_plan_approvals_hash_expiry
                    ON plan_approvals(plan_hash, expires_at);

                CREATE TABLE execution_audit (
                    audit_id TEXT PRIMARY KEY NOT NULL,
                    event_type TEXT NOT NULL,
                    plan_hash TEXT,
                    details_json TEXT NOT NULL,
                    occurred_at TEXT NOT NULL
                );

                CREATE INDEX idx_execution_audit_occurred_at
                    ON execution_audit(occurred_at);

                INSERT INTO schema_migrations (version, applied_at)
                VALUES (3, $appliedAt);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$appliedAt",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            currentVersion = 3;
        }

        if (currentVersion == 3)
        {
            using var transaction = connection.BeginTransaction();
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = """
                ALTER TABLE plan_approvals
                    ADD COLUMN approved_by TEXT NOT NULL DEFAULT 'legacy-unverified';

                UPDATE plan_approvals
                SET revoked_at = $appliedAt
                WHERE consumed_at IS NULL AND revoked_at IS NULL;

                INSERT INTO schema_migrations (version, applied_at)
                VALUES (4, $appliedAt);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$appliedAt",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            currentVersion = 4;
        }

        if (currentVersion == 4)
        {
            using var transaction = connection.BeginTransaction();
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = """
                CREATE TABLE mobile_pairing_invitations (
                    invitation_id TEXT PRIMARY KEY NOT NULL,
                    secret_hash TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    state TEXT NOT NULL CHECK (state IN ('CREATED', 'PENDING', 'COMPLETED', 'CANCELLED')),
                    request_id TEXT
                );

                CREATE TABLE mobile_pairing_requests (
                    request_id TEXT PRIMARY KEY NOT NULL,
                    invitation_id TEXT NOT NULL REFERENCES mobile_pairing_invitations(invitation_id),
                    device_name TEXT NOT NULL,
                    public_key TEXT NOT NULL,
                    public_key_sha256 TEXT NOT NULL,
                    polling_secret_hash TEXT NOT NULL,
                    status TEXT NOT NULL CHECK (status IN ('PENDING', 'PAIRED', 'REJECTED')),
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    device_id TEXT
                );

                CREATE INDEX idx_mobile_pairing_requests_status_expiry
                    ON mobile_pairing_requests(status, expires_at);

                CREATE TABLE mobile_paired_devices (
                    device_id TEXT PRIMARY KEY NOT NULL,
                    display_name TEXT NOT NULL,
                    public_key TEXT NOT NULL,
                    public_key_sha256 TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    last_used_at TEXT,
                    revoked_at TEXT
                );

                CREATE UNIQUE INDEX idx_mobile_active_device_key
                    ON mobile_paired_devices(public_key_sha256)
                    WHERE revoked_at IS NULL;

                INSERT INTO schema_migrations (version, applied_at)
                VALUES (5, $appliedAt);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$appliedAt",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            currentVersion = 5;
        }

        if (currentVersion == 5)
        {
            using var transaction = connection.BeginTransaction();
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = """
                CREATE TABLE mobile_request_nonces (
                    device_id TEXT NOT NULL REFERENCES mobile_paired_devices(device_id),
                    nonce TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    PRIMARY KEY (device_id, nonce)
                );

                CREATE INDEX idx_mobile_request_nonces_expiry
                    ON mobile_request_nonces(expires_at);

                INSERT INTO schema_migrations (version, applied_at)
                VALUES (6, $appliedAt);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$appliedAt",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not long value || value != 1)
        {
            throw new InvalidOperationException("SQLite health check returned an unexpected result.");
        }
    }

    private static async Task<int> GetCurrentVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
