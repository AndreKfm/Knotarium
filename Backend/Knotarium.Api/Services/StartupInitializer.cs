using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Security;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api.Services;

/// <summary>
/// Ordered startup work run once against the database before the host serves traffic. Three concerns,
/// deliberately kept as separate steps: bring the SQLite schema up to date (<see cref="MigrateSchema"/>),
/// verify the tamper-evident audit chain (<see cref="VerifyAuditChainAsync"/>), and heal legacy socket
/// mappings on stored graphs (<see cref="HealSocketMappingsAsync"/>).
/// </summary>
public static class StartupInitializer
{
    // Non-branch = nodes whose sole data output port is the generic one. Branch nodes (condition,
    // httpRequest, transform, merge, switch, scheduler, forLoop, imported OpenAPI) keep semantic port names.
    private static readonly HashSet<string> NonBranchNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "log", "delay", "setVariable", "subflow", "manualTrigger", "webhookTrigger"
    };

    /// <summary>
    /// Create the database if missing and, for SQLite, evolve an existing schema in place (EnsureCreated
    /// never alters an existing database). A pre-existing non-SQLite database is rejected upstream by the
    /// startup guard, so the ALTER/CREATE statements here are SQLite-only by design.
    /// </summary>
    public static void MigrateSchema(AppDbContext db)
    {
        var schemaWasCreated = db.Database.EnsureCreated();
        var isSqlite = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";

        // Schema evolution is implemented only for SQLite (the PRAGMA/ALTER block below); the pre-release plan
        // is to recreate the dev database on schema change rather than ship migrations. EnsureCreated never
        // alters an existing database, so a *pre-existing* non-SQLite database (e.g. PostgreSQL) can silently be
        // missing columns added since it was first created — and would then fail cryptically at query time.
        // Refuse to start instead. A freshly-created database (schemaWasCreated == true) is fine on any provider.
        if (!isSqlite && !schemaWasCreated)
        {
            throw new InvalidOperationException(
                $"Refusing to start: the '{db.Database.ProviderName}' database already exists, but automatic schema " +
                "evolution is implemented only for SQLite. This build has no migration path for other providers, so a " +
                "pre-existing database may be missing recently-added columns. Recreate the database (or add migrations) " +
                "before running against this provider.");
        }

        // EnsureCreated does not modify the schema if the SQLite db file already exists (missing newly added tables)
        if (isSqlite)
        {
            var connection = db.Database.GetDbConnection();
            var shouldCloseConnection = connection.State != ConnectionState.Open;
            if (shouldCloseConnection)
            {
                connection.Open();
            }

            try
            {
                // Switch the file to WAL once (persists in the file header, so every later EF/journal-writer
                // connection inherits it): readers don't block the single writer, and appends are cheaper.
                Knotarium.Infrastructure.Persistence.SqlitePragmas.EnableWal(connection);

                // Convert the file to incremental auto-vacuum (one-time VACUUM) so deleted rows can return
                // disk to the OS. Best-effort: a VACUUM needs free space and exclusive access, so if it can't
                // run right now, log and continue — retention still bounds logical growth and the next startup
                // retries the conversion. Never block startup on a disk-reclaim optimization.
                try
                {
                    Knotarium.Infrastructure.Persistence.SqlitePragmas.EnsureIncrementalAutoVacuum(connection);
                }
                catch (Exception vacuumEx)
                {
                    Console.Error.WriteLine(
                        "[WARN] Could not enable incremental auto-vacuum (will retry next startup): " + vacuumEx.Message);
                }

                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA table_info('ExecutionInstances');";

                using var reader = command.ExecuteReader();
                var hasWorkflowVersionIdColumn = false;
                var hasVariableStateColumn = false;
                var hasTriggerOriginColumn = false;
                var hasReplayOfExecutionIdColumn = false;
                var hasReplayFromNodeIdColumn = false;
                var hasErrorOfExecutionIdColumn = false;
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (string.Equals(columnName, "WorkflowVersionId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasWorkflowVersionIdColumn = true;
                    }

                    if (string.Equals(columnName, "VariableState", StringComparison.OrdinalIgnoreCase))
                    {
                        hasVariableStateColumn = true;
                    }

                    if (string.Equals(columnName, "TriggerOrigin", StringComparison.OrdinalIgnoreCase))
                    {
                        hasTriggerOriginColumn = true;
                    }

                    if (string.Equals(columnName, "ReplayOfExecutionId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasReplayOfExecutionIdColumn = true;
                    }

                    if (string.Equals(columnName, "ReplayFromNodeId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasReplayFromNodeIdColumn = true;
                    }

                    if (string.Equals(columnName, "ErrorOfExecutionId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasErrorOfExecutionIdColumn = true;
                    }
                }

                if (!hasWorkflowVersionIdColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ExecutionInstances ADD COLUMN WorkflowVersionId TEXT NULL;");
                }

                if (!hasVariableStateColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ExecutionInstances ADD COLUMN VariableState TEXT NULL;");
                }

                if (!hasTriggerOriginColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ExecutionInstances ADD COLUMN TriggerOrigin TEXT NOT NULL DEFAULT 'manual';");
                }

                if (!hasReplayOfExecutionIdColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ExecutionInstances ADD COLUMN ReplayOfExecutionId TEXT NULL;");
                }

                if (!hasReplayFromNodeIdColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ExecutionInstances ADD COLUMN ReplayFromNodeId TEXT NULL;");
                }

                if (!hasErrorOfExecutionIdColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ExecutionInstances ADD COLUMN ErrorOfExecutionId TEXT NULL;");
                }

                using var workflowColumnCommand = connection.CreateCommand();
                workflowColumnCommand.CommandText = "PRAGMA table_info('WorkflowDefinitions');";

                using var workflowReader = workflowColumnCommand.ExecuteReader();
                var hasIsEnabledColumn = false;
                var hasIsArchivedColumn = false;
                var hasMetadataColumn = false;
                while (workflowReader.Read())
                {
                    var columnName = workflowReader.GetString(1);
                    if (string.Equals(columnName, "IsEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        hasIsEnabledColumn = true;
                    }

                    if (string.Equals(columnName, "IsArchived", StringComparison.OrdinalIgnoreCase))
                    {
                        hasIsArchivedColumn = true;
                    }

                    if (string.Equals(columnName, "Metadata", StringComparison.OrdinalIgnoreCase))
                    {
                        hasMetadataColumn = true;
                    }
                }

                workflowReader.Close();

                if (!hasIsEnabledColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE WorkflowDefinitions ADD COLUMN IsEnabled INTEGER NOT NULL DEFAULT 1;");
                }

                if (!hasIsArchivedColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE WorkflowDefinitions ADD COLUMN IsArchived INTEGER NOT NULL DEFAULT 0;");
                }

                if (!hasMetadataColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE WorkflowDefinitions ADD COLUMN Metadata TEXT NULL;");
                }

                using var nodeStateColumnCommand = connection.CreateCommand();
                nodeStateColumnCommand.CommandText = "PRAGMA table_info('NodeStates');";

                using var nodeStateReader = nodeStateColumnCommand.ExecuteReader();
                var hasVariablesBeforeColumn = false;
                while (nodeStateReader.Read())
                {
                    var columnName = nodeStateReader.GetString(1);
                    if (string.Equals(columnName, "VariablesBefore", StringComparison.OrdinalIgnoreCase))
                    {
                        hasVariablesBeforeColumn = true;
                    }
                }

                nodeStateReader.Close();

                if (!hasVariablesBeforeColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE NodeStates ADD COLUMN VariablesBefore TEXT NULL;");
                }

                using var serverConfigColumnCommand = connection.CreateCommand();
                serverConfigColumnCommand.CommandText = "PRAGMA table_info('ServerConfigs');";
                using var serverConfigReader = serverConfigColumnCommand.ExecuteReader();
                var hasAllowInsecureColumn = false;
                while (serverConfigReader.Read())
                {
                    if (string.Equals(serverConfigReader.GetString(1), "AllowInsecureCertificate", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAllowInsecureColumn = true;
                    }
                }
                serverConfigReader.Close();

                if (!hasAllowInsecureColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ServerConfigs ADD COLUMN AllowInsecureCertificate INTEGER NOT NULL DEFAULT 0;");
                }

                using var workflowVersionColumnCommand = connection.CreateCommand();
                workflowVersionColumnCommand.CommandText = "PRAGMA table_info('WorkflowVersions');";

                using var workflowVersionReader = workflowVersionColumnCommand.ExecuteReader();
                var hasOriginColumn = false;
                var hasSourceVersionIdColumn = false;
                var hasCreatedByColumn = false;
                var hasLabelColumn = false;
                var hasCreationReasonColumn = false;
                while (workflowVersionReader.Read())
                {
                    var columnName = workflowVersionReader.GetString(1);
                    if (string.Equals(columnName, "Origin", StringComparison.OrdinalIgnoreCase))
                    {
                        hasOriginColumn = true;
                    }

                    if (string.Equals(columnName, "SourceVersionId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSourceVersionIdColumn = true;
                    }

                    if (string.Equals(columnName, "CreatedBy", StringComparison.OrdinalIgnoreCase))
                    {
                        hasCreatedByColumn = true;
                    }

                    if (string.Equals(columnName, "Label", StringComparison.OrdinalIgnoreCase))
                    {
                        hasLabelColumn = true;
                    }

                    if (string.Equals(columnName, "CreationReason", StringComparison.OrdinalIgnoreCase))
                    {
                        hasCreationReasonColumn = true;
                    }
                }

                workflowVersionReader.Close();

                if (!hasOriginColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE WorkflowVersions ADD COLUMN Origin TEXT NOT NULL DEFAULT 'Published';");
                }

                if (!hasSourceVersionIdColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE WorkflowVersions ADD COLUMN SourceVersionId TEXT NULL;");
                }

                if (!hasCreatedByColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE WorkflowVersions ADD COLUMN CreatedBy TEXT NULL;");
                }

                if (!hasLabelColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE WorkflowVersions ADD COLUMN Label TEXT NULL;");
                }

                if (!hasCreationReasonColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE WorkflowVersions ADD COLUMN CreationReason TEXT NULL;");
                }

                using var activeVersionColumnCommand = connection.CreateCommand();
                activeVersionColumnCommand.CommandText = "PRAGMA table_info('ActiveWorkflowVersions');";

                using var activeVersionReader = activeVersionColumnCommand.ExecuteReader();
                var hasActivatedByColumn = false;
                var hasConcurrencyTokenColumn = false;
                while (activeVersionReader.Read())
                {
                    var columnName = activeVersionReader.GetString(1);
                    if (string.Equals(columnName, "ActivatedBy", StringComparison.OrdinalIgnoreCase))
                    {
                        hasActivatedByColumn = true;
                    }

                    if (string.Equals(columnName, "ConcurrencyToken", StringComparison.OrdinalIgnoreCase))
                    {
                        hasConcurrencyTokenColumn = true;
                    }
                }

                activeVersionReader.Close();

                if (!hasActivatedByColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ActiveWorkflowVersions ADD COLUMN ActivatedBy TEXT NULL;");
                }

                if (!hasConcurrencyTokenColumn)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE ActiveWorkflowVersions ADD COLUMN ConcurrencyToken TEXT NOT NULL DEFAULT '';");
                }
            }
            finally
            {
                if (shouldCloseConnection)
                {
                    connection.Close();
                }
            }

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ActiveWorkers (
                    Id TEXT NOT NULL PRIMARY KEY,
                    LastHeartbeat INTEGER NOT NULL
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS AuditEntries (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Action TEXT NOT NULL,
                    Actor TEXT NOT NULL,
                    Timestamp INTEGER NOT NULL,
                    Details TEXT NOT NULL,
                    PreviousHash TEXT NOT NULL,
                    EntryHash TEXT NOT NULL
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS NotificationChannels (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    EncryptedConfig TEXT NOT NULL,
                    IsDefaultFailureAlert INTEGER NOT NULL DEFAULT 0,
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS AppSettings (
                    Key TEXT NOT NULL PRIMARY KEY,
                    Value TEXT NULL
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS NodePackages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    DisplayName TEXT NOT NULL,
                    Category TEXT NOT NULL
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS NodePackageVersions (
                    Id TEXT NOT NULL PRIMARY KEY,
                    NodePackageId TEXT NOT NULL,
                    Version TEXT NOT NULL,
                    ManifestJson TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    CompiledAssembly BLOB,
                    Signature TEXT,
                    Capabilities TEXT,
                    CreatedAt INTEGER NOT NULL,
                    FOREIGN KEY(NodePackageId) REFERENCES NodePackages(Id) ON DELETE CASCADE
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS WorkflowVersions (
                    Id TEXT NOT NULL PRIMARY KEY,
                    WorkflowDefinitionId TEXT NOT NULL,
                    VersionNumber INTEGER NOT NULL,
                    Nodes TEXT NOT NULL,
                    Edges TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    Origin TEXT NOT NULL DEFAULT 'Published',
                    SourceVersionId TEXT NULL,
                    CreatedBy TEXT NULL,
                    Label TEXT NULL,
                    CreationReason TEXT NULL,
                    FOREIGN KEY(WorkflowDefinitionId) REFERENCES WorkflowDefinitions(Id) ON DELETE CASCADE
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkflowVersions_WorkflowDefinitionId_VersionNumber
                ON WorkflowVersions (WorkflowDefinitionId, VersionNumber);
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ActiveWorkflowVersions (
                    WorkflowDefinitionId TEXT NOT NULL PRIMARY KEY,
                    WorkflowVersionId TEXT NOT NULL,
                    ActivatedAtUtc INTEGER NOT NULL,
                    ActivatedBy TEXT NULL,
                    ConcurrencyToken TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY(WorkflowDefinitionId) REFERENCES WorkflowDefinitions(Id) ON DELETE CASCADE,
                    FOREIGN KEY(WorkflowVersionId) REFERENCES WorkflowVersions(Id)
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS WorkflowVersionActivations (
                    Id TEXT NOT NULL PRIMARY KEY,
                    WorkflowDefinitionId TEXT NOT NULL,
                    WorkflowVersionId TEXT NOT NULL,
                    ActivatedAtUtc INTEGER NOT NULL,
                    ActivatedBy TEXT NULL,
                    ActivationReason TEXT NULL,
                    RestoredFromVersionId TEXT NULL,
                    PreviousActiveVersionId TEXT NULL,
                    CorrelationId TEXT NULL,
                    FOREIGN KEY(WorkflowDefinitionId) REFERENCES WorkflowDefinitions(Id) ON DELETE CASCADE,
                    FOREIGN KEY(WorkflowVersionId) REFERENCES WorkflowVersions(Id)
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE INDEX IF NOT EXISTS IX_WorkflowVersionActivations_WorkflowDefinitionId_ActivatedAtUtc
                ON WorkflowVersionActivations (WorkflowDefinitionId, ActivatedAtUtc);
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS CorrelationTokens (
                    Id TEXT NOT NULL PRIMARY KEY,
                    HashedToken TEXT NOT NULL,
                    ExecutionInstanceId TEXT NOT NULL,
                    NodeId TEXT NOT NULL,
                    ExpiresAtUtc INTEGER NOT NULL,
                    CreatedAtUtc INTEGER NOT NULL,
                    ConsumedAtUtc INTEGER NULL,
                    FOREIGN KEY(ExecutionInstanceId) REFERENCES ExecutionInstances(Id) ON DELETE CASCADE
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE UNIQUE INDEX IF NOT EXISTS IX_CorrelationTokens_HashedToken
                ON CorrelationTokens (HashedToken);
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ExecutionWorkItems (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ExecutionInstanceId TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    Payload TEXT NOT NULL,
                    NotBeforeUtc INTEGER NULL,
                    Status TEXT NOT NULL,
                    CreatedAtUtc INTEGER NOT NULL,
                    ProcessedAtUtc INTEGER NULL,
                    FOREIGN KEY(ExecutionInstanceId) REFERENCES ExecutionInstances(Id) ON DELETE CASCADE
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE INDEX IF NOT EXISTS IX_ExecutionWorkItems_Status_NotBeforeUtc_CreatedAtUtc
                ON ExecutionWorkItems (Status, NotBeforeUtc, CreatedAtUtc);
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS NodeRetryStates (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ExecutionInstanceId TEXT NOT NULL,
                    NodeId TEXT NOT NULL,
                    AttemptNumber INTEGER NOT NULL,
                    NextRetryAtUtc INTEGER NOT NULL,
                    SanitizedFailureMessage TEXT NOT NULL,
                    FOREIGN KEY(ExecutionInstanceId) REFERENCES ExecutionInstances(Id) ON DELETE CASCADE
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE UNIQUE INDEX IF NOT EXISTS IX_NodeRetryStates_ExecutionInstanceId_NodeId
                ON NodeRetryStates (ExecutionInstanceId, NodeId);
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS Schedules (
                    Id TEXT NOT NULL PRIMARY KEY,
                    WorkflowDefinitionId TEXT NOT NULL,
                    CronExpression TEXT NOT NULL,
                    TimeZoneId TEXT NOT NULL,
                    NextFireAtUtc INTEGER NOT NULL,
                    IsActive INTEGER NOT NULL,
                    FOREIGN KEY(WorkflowDefinitionId) REFERENCES WorkflowDefinitions(Id) ON DELETE CASCADE
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ScheduleFires (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ScheduleId TEXT NOT NULL,
                    PlannedFireAtUtc INTEGER NOT NULL,
                    FiredAtUtc INTEGER NOT NULL,
                    ExecutionInstanceId TEXT NULL,
                    Status TEXT NOT NULL,
                    FOREIGN KEY(ExecutionInstanceId) REFERENCES ExecutionInstances(Id) ON DELETE SET NULL
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE UNIQUE INDEX IF NOT EXISTS IX_ScheduleFires_ScheduleId_PlannedFireAtUtc
                ON ScheduleFires (ScheduleId, PlannedFireAtUtc);
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS PollingTriggers (
                    Id TEXT NOT NULL PRIMARY KEY,
                    WorkflowDefinitionId TEXT NOT NULL,
                    IntervalSeconds INTEGER NOT NULL,
                    NextPollAtUtc INTEGER NOT NULL,
                    ConfigJson TEXT NOT NULL,
                    Cursor TEXT NULL,
                    IsActive INTEGER NOT NULL,
                    LastPolledAtUtc INTEGER NULL,
                    LastError TEXT NULL,
                    FOREIGN KEY(WorkflowDefinitionId) REFERENCES WorkflowDefinitions(Id) ON DELETE CASCADE
                );
            ");

            // Indexes for the two central poll loops, which scan for active triggers due to fire/poll. Without
            // these, each 10s evaluation is a full table scan of Schedules / PollingTriggers.
            db.Database.ExecuteSqlRaw(@"
                CREATE INDEX IF NOT EXISTS IX_Schedules_IsActive_NextFireAtUtc
                ON Schedules (IsActive, NextFireAtUtc);
            ");
            db.Database.ExecuteSqlRaw(@"
                CREATE INDEX IF NOT EXISTS IX_PollingTriggers_IsActive_NextPollAtUtc
                ON PollingTriggers (IsActive, NextPollAtUtc);
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS Users (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Username TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    Role TEXT NOT NULL DEFAULT 'admin',
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL
                );
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Username ON Users (Username);
            ");

            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS UserTemplates (
                    TemplateId TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Author TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    TemplateVersion TEXT NOT NULL,
                    ManifestJson TEXT NOT NULL,
                    ArchiveBase64 TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL
                );
            ");
        }
    }

    /// <summary>Verify the append-only audit chain; a broken hash chain aborts startup.</summary>
    public static async Task VerifyAuditChainAsync(AppDbContext db)
    {
        var auditEntries = await db.AuditEntries
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.Id)
            .ToListAsync();

        if (auditEntries.Count > 0 && !AuditHashChain.VerifyChain(auditEntries))
        {
            throw new InvalidOperationException("Audit chain verification failed during startup.");
        }
    }

    /// <summary>
    /// Self-healing migration for socket mappings. Two idempotent rewrites, keyed off the source node type
    /// so genuine branch ports are never touched:
    ///   - legacy "default" output -> "true" (condition) / "result" (non-branch) / "success" (other branches)
    ///   - legacy "success" output on a non-branch node -> "result" (the renamed generic data port)
    ///   - legacy "default" input  -> "in"
    /// </summary>
    public static async Task HealSocketMappingsAsync(AppDbContext db, ILogger logger)
    {
        try
        {
            var workflows = await db.WorkflowDefinitions.ToListAsync();
            var migratedWfCount = 0;
            foreach (var wf in workflows)
            {
                var newEdges = MigrateEdges(wf.Nodes, wf.Edges, out var needsMigration);
                if (needsMigration)
                {
                    db.Entry(wf).CurrentValues.SetValues(wf with { Edges = newEdges });
                    migratedWfCount++;
                }
            }

            var versions = await db.WorkflowVersions.ToListAsync();
            var migratedVerCount = 0;
            foreach (var ver in versions)
            {
                var newEdges = MigrateEdges(ver.Nodes, ver.Edges, out var needsMigration);
                if (needsMigration)
                {
                    db.Entry(ver).CurrentValues.SetValues(ver with { Edges = newEdges });
                    migratedVerCount++;
                }
            }

            if (migratedWfCount > 0 || migratedVerCount > 0)
            {
                await db.SaveChangesAsync();
                logger.LogInformation(
                    "Healed {WorkflowCount} workflow definitions and {VersionCount} workflow versions (default/success socket mappings -> result/in).",
                    migratedWfCount, migratedVerCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to run socket healing migration.");
        }
    }

    private static string CanonicalizeOutput(string? sourceType, string output)
    {
        if (string.Equals(output, "default", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceType != null && string.Equals(sourceType, "condition", StringComparison.OrdinalIgnoreCase))
                return "true";
            return sourceType != null && NonBranchNodeTypes.Contains(sourceType) ? "result" : "success";
        }

        if (string.Equals(output, "success", StringComparison.OrdinalIgnoreCase) &&
            sourceType != null && NonBranchNodeTypes.Contains(sourceType))
        {
            return "result";
        }

        return output;
    }

    private static List<EdgeDefinition> MigrateEdges(IReadOnlyList<NodeDefinition> nodes, IReadOnlyList<EdgeDefinition> edges, out bool changed)
    {
        changed = false;
        var newEdges = new List<EdgeDefinition>();
        foreach (var edge in edges)
        {
            var sourceType = nodes.FirstOrDefault(n => n.Id == edge.From)?.Type;
            var output = CanonicalizeOutput(sourceType, edge.Output);
            var input = string.Equals(edge.Input, "default", StringComparison.OrdinalIgnoreCase) ? "in" : edge.Input;

            if (!string.Equals(output, edge.Output, StringComparison.Ordinal) ||
                !string.Equals(input, edge.Input, StringComparison.Ordinal))
            {
                changed = true;
            }

            newEdges.Add(new EdgeDefinition(edge.Id, edge.From, output, edge.To, input));
        }
        return newEdges;
    }
}
