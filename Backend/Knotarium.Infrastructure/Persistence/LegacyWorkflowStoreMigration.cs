// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// One-time relocation of workflow draft files from the historical per-user store
/// (%APPDATA%/Knotarium) into the shared machine-wide data directory. Idempotent and
/// non-destructive: files already present in the target are never overwritten, so re-running
/// (or racing an old build) cannot lose data. Runs at host startup before the first
/// <see cref="FileWorkflowStore"/> is constructed.
/// </summary>
public static class LegacyWorkflowStoreMigration
{
    /// <summary>
    /// Moves workflows/*.json and groups.json from the legacy per-user store into
    /// <paramref name="targetStoreFolder"/> when they are missing there. The legacy store is the
    /// AppData of the account running this process — for a Windows service that is the service
    /// account's profile, which is exactly where its own older builds wrote the files.
    /// </summary>
    public static void Migrate(string targetStoreFolder, ILogger logger)
        => Migrate(
            targetStoreFolder,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Knotarium"),
            logger);

    /// <summary>
    /// Same as <see cref="Migrate(string, ILogger)"/> with an explicit legacy store folder
    /// (exposed for tests).
    /// </summary>
    public static void Migrate(string targetStoreFolder, string legacyStoreFolder, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(targetStoreFolder))
        {
            return;
        }

        var target = Path.GetFullPath(targetStoreFolder);
        var legacy = Path.GetFullPath(legacyStoreFolder);
        if (string.Equals(target, legacy, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(legacy))
        {
            return;
        }

        try
        {
            var movedFiles = 0;

            var legacyWorkflowsDir = Path.Combine(legacy, "workflows");
            if (Directory.Exists(legacyWorkflowsDir))
            {
                var targetWorkflowsDir = Path.Combine(target, "workflows");
                Directory.CreateDirectory(targetWorkflowsDir);
                foreach (var file in Directory.GetFiles(legacyWorkflowsDir, "*.json"))
                {
                    var destination = Path.Combine(targetWorkflowsDir, Path.GetFileName(file));
                    if (File.Exists(destination))
                    {
                        logger.LogWarning(
                            "Skipping legacy workflow file {File}: a file with the same name already exists in {Target}",
                            file, targetWorkflowsDir);
                        continue;
                    }

                    File.Move(file, destination);
                    movedFiles++;
                }

                if (Directory.GetFileSystemEntries(legacyWorkflowsDir).Length == 0)
                {
                    Directory.Delete(legacyWorkflowsDir);
                }
            }

            var legacyGroupsFile = Path.Combine(legacy, "groups.json");
            if (File.Exists(legacyGroupsFile))
            {
                var destinationGroupsFile = Path.Combine(target, "groups.json");
                if (File.Exists(destinationGroupsFile))
                {
                    logger.LogWarning(
                        "Skipping legacy groups file {File}: {Target} already exists",
                        legacyGroupsFile, destinationGroupsFile);
                }
                else
                {
                    Directory.CreateDirectory(target);
                    File.Move(legacyGroupsFile, destinationGroupsFile);
                    movedFiles++;
                }
            }

            if (movedFiles > 0)
            {
                logger.LogInformation(
                    "Migrated {Count} workflow store file(s) from the legacy per-user store {Legacy} to the shared data directory {Target}",
                    movedFiles, legacy, target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "Could not migrate the legacy workflow store from {Legacy}; continuing with the shared data directory only",
                legacy);
        }
    }
}
