using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence.OpenApi;

namespace Knotarium.Infrastructure.Persistence;

public static class ValueComparerHelper
{
    public static ValueComparer<T> CreateJsonComparer<T>()
    {
        return new ValueComparer<T>(
            (c1, c2) => JsonSerializer.Serialize(c1, PersistenceJsonOptions.Default) == JsonSerializer.Serialize(c2, PersistenceJsonOptions.Default),
            c => c == null ? 0 : JsonSerializer.Serialize(c, PersistenceJsonOptions.Default).GetHashCode(),
            c => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(c, PersistenceJsonOptions.Default), PersistenceJsonOptions.Default)!
        );
    }
}

public class AppDbContext : DbContext
{
    public DbSet<WorkflowDefinition> WorkflowDefinitions { get; set; } = null!;
    public DbSet<ExecutionInstance> ExecutionInstances { get; set; } = null!;
    public DbSet<NodeState> NodeStates { get; set; } = null!;
    public DbSet<ExecutionJournal> JournalEntries { get; set; } = null!;
    public DbSet<WorkflowVersion> WorkflowVersions { get; set; } = null!;
    public DbSet<NodePackage> NodePackages { get; set; } = null!;
    public DbSet<NodePackageVersion> NodePackageVersions { get; set; } = null!;
    public DbSet<Credential> Credentials { get; set; } = null!;
    public DbSet<UserAccount> Users { get; set; } = null!;
    public DbSet<UserTemplate> UserTemplates { get; set; } = null!;
    public DbSet<NotificationChannel> NotificationChannels { get; set; } = null!;
    public DbSet<AuditEntry> AuditEntries { get; set; } = null!;
    public DbSet<ActiveWorker> ActiveWorkers { get; set; } = null!;
    public DbSet<ActiveWorkflowVersion> ActiveWorkflowVersions { get; set; } = null!;
    public DbSet<WorkflowVersionActivation> WorkflowVersionActivations { get; set; } = null!;
    public DbSet<CorrelationToken> CorrelationTokens { get; set; } = null!;
    public DbSet<ExecutionWorkItem> ExecutionWorkItems { get; set; } = null!;
    public DbSet<NodeRetryState> NodeRetryStates { get; set; } = null!;
    public DbSet<ScheduleFire> ScheduleFires { get; set; } = null!;
    public DbSet<Schedule> Schedules { get; set; } = null!;
    public DbSet<PollingTrigger> PollingTriggers { get; set; } = null!;
    public DbSet<OpenApiSpecEntity> OpenApiSpecs { get; set; } = null!;
    public DbSet<OpenApiSpecVersionEntity> OpenApiSpecVersions { get; set; } = null!;
    public DbSet<ServerConfigEntity> ServerConfigs { get; set; } = null!;
    public DbSet<AppSetting> AppSettings { get; set; } = null!;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Value Converters for Custom Domain Types
        var workflowIdConverter = new ValueConverter<WorkflowDefinitionId, string>(
            v => v.Value,
            v => new WorkflowDefinitionId(v));

        var workflowVersionIdConverter = new ValueConverter<WorkflowVersionId, Guid>(
            v => v.Value,
            v => new WorkflowVersionId(v));

        var nullableWorkflowVersionIdConverter = new ValueConverter<WorkflowVersionId?, Guid?>(
            v => v.HasValue ? v.Value.Value : null,
            v => v.HasValue ? new WorkflowVersionId(v.Value) : null);

        var nodePackageIdConverter = new ValueConverter<NodePackageId, string>(
            v => v.Value,
            v => new NodePackageId(v));

        var nodePackageVersionIdConverter = new ValueConverter<NodePackageVersionId, Guid>(
            v => v.Value,
            v => new NodePackageVersionId(v));

        var executionInstanceIdConverter = new ValueConverter<ExecutionInstanceId, Guid>(
            v => v.Value,
            v => new ExecutionInstanceId(v));

        var nodeIdConverter = new ValueConverter<NodeId, string>(
            v => v.Value,
            v => NodeId.Create(v));

        var nullableNodeIdConverter = new ValueConverter<NodeId?, string?>(
            v => v.HasValue ? v.Value.Value : null,
            v => string.IsNullOrEmpty(v) ? null : NodeId.Create(v));

        // 1. WorkflowDefinition Configuration
        modelBuilder.Entity<WorkflowDefinition>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Id)
                .HasConversion(workflowIdConverter)
                .ValueGeneratedNever();

            entity.Property(w => w.Name)
                .IsRequired();

            entity.Property(w => w.Nodes)
                .HasConversion(new JsonValueConverter<IReadOnlyList<NodeDefinition>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<IReadOnlyList<NodeDefinition>>());

            entity.Property(w => w.Edges)
                .HasConversion(new JsonValueConverter<IReadOnlyList<EdgeDefinition>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<IReadOnlyList<EdgeDefinition>>());

            entity.Property(w => w.IsEnabled)
                .HasDefaultValue(true);

            entity.Property(w => w.IsArchived)
                .HasDefaultValue(false);

            // Persist workflow metadata (group membership + failure-alert config) as JSON. Previously
            // ignored, which silently dropped a workflow's group/alert assignment on every save.
            entity.Property(w => w.Metadata)
                .HasConversion(new JsonValueConverter<WorkflowMetadata?>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<WorkflowMetadata?>());
        });

        // 2. ExecutionInstance Configuration
        modelBuilder.Entity<ExecutionInstance>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                .HasConversion(executionInstanceIdConverter)
                .ValueGeneratedNever();

            entity.Property(e => e.WorkflowDefinitionId)
                .HasConversion(workflowIdConverter)
                .IsRequired();

            entity.Property(e => e.WorkflowVersionId)
                .HasConversion(nullableWorkflowVersionIdConverter);

            entity.Property(e => e.ReplayOfExecutionId)
                .HasConversion(new ValueConverter<ExecutionInstanceId?, Guid?>(
                    v => v.HasValue ? v.Value.Value : null,
                    v => v.HasValue ? new ExecutionInstanceId(v.Value) : null));

            entity.Property(e => e.ReplayFromNodeId)
                .HasConversion(nullableNodeIdConverter);

            entity.Property(e => e.ErrorOfExecutionId)
                .HasConversion(new ValueConverter<ExecutionInstanceId?, Guid?>(
                    v => v.HasValue ? v.Value.Value : null,
                    v => v.HasValue ? new ExecutionInstanceId(v.Value) : null));

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(e => e.TriggerOrigin)
                .IsRequired();

            entity.Property(e => e.GlobalVariables)
                .HasConversion(new JsonValueConverter<Dictionary<string, object>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<Dictionary<string, object>>());

            entity.HasMany(e => e.NodeStates)
                .WithOne()
                .HasForeignKey(ns => ns.ExecutionInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.JournalEntries)
                .WithOne()
                .HasForeignKey(j => j.ExecutionInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 3. NodeState Configuration
        modelBuilder.Entity<NodeState>(entity =>
        {
            entity.HasKey(ns => ns.Id);
            entity.Property(ns => ns.Id)
                .ValueGeneratedNever();

            entity.Property(ns => ns.ExecutionInstanceId)
                .HasConversion(executionInstanceIdConverter)
                .IsRequired();

            entity.Property(ns => ns.NodeId)
                .HasConversion(nodeIdConverter)
                .IsRequired();

            entity.Property(ns => ns.Status)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(ns => ns.Inputs)
                .HasConversion(new JsonValueConverter<Dictionary<string, object>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<Dictionary<string, object>>());

            entity.Property(ns => ns.Outputs)
                .HasConversion(new JsonValueConverter<Dictionary<string, object>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<Dictionary<string, object>>());
        });

        // 4. ExecutionJournal Configuration
        modelBuilder.Entity<ExecutionJournal>(entity =>
        {
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Id)
                .ValueGeneratedNever();

            entity.Property(j => j.ExecutionInstanceId)
                .HasConversion(executionInstanceIdConverter)
                .IsRequired();

            entity.Property(j => j.NodeId)
                .HasConversion(nullableNodeIdConverter);

            entity.Property(j => j.Data)
                .HasConversion(new JsonValueConverter<Dictionary<string, object>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<Dictionary<string, object>>());
        });

        // 5. WorkflowVersion Configuration
        modelBuilder.Entity<WorkflowVersion>(entity =>
        {
            entity.HasKey(wv => wv.Id);
            entity.Property(wv => wv.Id)
                .HasConversion(workflowVersionIdConverter)
                .ValueGeneratedNever();

            entity.Property(wv => wv.WorkflowDefinitionId)
                .HasConversion(workflowIdConverter)
                .IsRequired();

            entity.Property(wv => wv.Nodes)
                .HasConversion(new JsonValueConverter<IReadOnlyList<NodeDefinition>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<IReadOnlyList<NodeDefinition>>());

            entity.Property(wv => wv.Edges)
                .HasConversion(new JsonValueConverter<IReadOnlyList<EdgeDefinition>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<IReadOnlyList<EdgeDefinition>>());

            entity.Property(wv => wv.Origin)
                .HasConversion<string>()
                .HasDefaultValue(WorkflowVersionOrigin.Published)
                .IsRequired();

            entity.Property(wv => wv.SourceVersionId)
                .HasConversion(nullableWorkflowVersionIdConverter);

            // Prevents the `max + 1` race from minting duplicate version numbers for a workflow.
            entity.HasIndex(wv => new { wv.WorkflowDefinitionId, wv.VersionNumber })
                .IsUnique();
        });

        // 6. NodePackage Configuration
        modelBuilder.Entity<NodePackage>(entity =>
        {
            entity.HasKey(np => np.Id);
            entity.Property(np => np.Id)
                .HasConversion(nodePackageIdConverter)
                .ValueGeneratedNever();

            entity.Property(np => np.DisplayName)
                .IsRequired();

            entity.Property(np => np.Category)
                .IsRequired();

            entity.HasMany(np => np.Versions)
                .WithOne()
                .HasForeignKey(npv => npv.NodePackageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 7. NodePackageVersion Configuration
        modelBuilder.Entity<NodePackageVersion>(entity =>
        {
            entity.HasKey(npv => npv.Id);
            entity.Property(npv => npv.Id)
                .HasConversion(nodePackageVersionIdConverter)
                .ValueGeneratedNever();

            entity.Property(npv => npv.NodePackageId)
                .HasConversion(nodePackageIdConverter)
                .IsRequired();

            entity.Property(npv => npv.Version)
                .IsRequired();

            entity.Property(npv => npv.ManifestJson)
                .IsRequired();

            entity.Property(npv => npv.Source)
                .IsRequired();

            entity.Property(npv => npv.Capabilities)
                .HasConversion(new JsonValueConverter<IReadOnlyList<string>>())
                .Metadata.SetValueComparer(ValueComparerHelper.CreateJsonComparer<IReadOnlyList<string>>());
        });

        // 8. Credential Configuration
        modelBuilder.Entity<Credential>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id)
                .ValueGeneratedNever();

            entity.Property(c => c.Name)
                .IsRequired();

            entity.Property(c => c.EncryptedValue)
                .IsRequired();
        });

        // 8a. UserAccount Configuration — login accounts. Username is unique (case-insensitive matching
        // is enforced in the service layer, which normalizes to lower-case before lookup/insert).
        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedNever();
            entity.Property(u => u.Username).IsRequired();
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.Role).IsRequired();
            entity.HasIndex(u => u.Username).IsUnique();
        });

        // 8b. UserTemplate Configuration — TemplateId is the key, so re-saving the same template upserts
        // rather than duplicating (the length-1-per-id invariant is DB-enforced, not app-enforced).
        modelBuilder.Entity<UserTemplate>(entity =>
        {
            entity.HasKey(t => t.TemplateId);
            entity.Property(t => t.TemplateId)
                .ValueGeneratedNever();

            entity.Property(t => t.Name)
                .IsRequired();

            entity.Property(t => t.ManifestJson)
                .IsRequired();

            entity.Property(t => t.ArchiveBase64)
                .IsRequired();
        });

        // 8b. NotificationChannel Configuration
        modelBuilder.Entity<NotificationChannel>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id)
                .ValueGeneratedNever();

            entity.Property(c => c.Name)
                .IsRequired();

            entity.Property(c => c.Type)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(c => c.EncryptedConfig)
                .IsRequired();

            entity.Property(c => c.IsDefaultFailureAlert)
                .HasDefaultValue(false);
        });

        // 9. AuditEntry Configuration
        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(ae => ae.Id);
            entity.Property(ae => ae.Id)
                .ValueGeneratedNever();

            entity.Property(ae => ae.Action)
                .IsRequired();

            entity.Property(ae => ae.Actor)
                .IsRequired();

            entity.Property(ae => ae.Details)
                .IsRequired();

            entity.Property(ae => ae.PreviousHash)
                .IsRequired();

            entity.Property(ae => ae.EntryHash)
                .IsRequired();
        });

        // 10. ActiveWorker Configuration
        modelBuilder.Entity<ActiveWorker>(entity =>
        {
            entity.HasKey(aw => aw.Id);
            entity.Property(aw => aw.Id)
                .ValueGeneratedNever();
        });

        // 11. CorrelationToken Configuration
        modelBuilder.Entity<ActiveWorkflowVersion>(entity =>
        {
            entity.HasKey(item => item.WorkflowDefinitionId);
            entity.Property(item => item.WorkflowDefinitionId)
                .HasConversion(workflowIdConverter)
                .ValueGeneratedNever();

            entity.Property(item => item.WorkflowVersionId)
                .HasConversion(workflowVersionIdConverter)
                .IsRequired();

            entity.Property(item => item.ConcurrencyToken)
                .IsConcurrencyToken()
                .IsRequired();
        });

        // 11b. WorkflowVersionActivation Configuration (append-only activation log)
        modelBuilder.Entity<WorkflowVersionActivation>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id)
                .ValueGeneratedNever();

            entity.Property(item => item.WorkflowDefinitionId)
                .HasConversion(workflowIdConverter)
                .IsRequired();

            entity.Property(item => item.WorkflowVersionId)
                .HasConversion(workflowVersionIdConverter)
                .IsRequired();

            entity.Property(item => item.RestoredFromVersionId)
                .HasConversion(nullableWorkflowVersionIdConverter);

            entity.Property(item => item.PreviousActiveVersionId)
                .HasConversion(nullableWorkflowVersionIdConverter);

            entity.HasIndex(item => new { item.WorkflowDefinitionId, item.ActivatedAtUtc });
        });

        // 11. CorrelationToken Configuration
        modelBuilder.Entity<CorrelationToken>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.Property(c => c.ExecutionInstanceId).HasConversion(executionInstanceIdConverter).IsRequired();
            entity.Property(c => c.NodeId).HasConversion(nodeIdConverter).IsRequired();
            entity.HasIndex(c => c.HashedToken).IsUnique();
            entity.Property(c => c.HashedToken).IsRequired().HasMaxLength(64);
            entity.Property(c => c.ExpiresAtUtc).IsRequired();
            entity.Property(c => c.CreatedAtUtc).IsRequired();
            entity.Property(c => c.ConsumedAtUtc);
        });

        // 12. ExecutionWorkItem Configuration
        modelBuilder.Entity<ExecutionWorkItem>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Id).ValueGeneratedNever();
            entity.Property(w => w.ExecutionInstanceId).HasConversion(executionInstanceIdConverter).IsRequired();
            entity.Property(w => w.Status).HasConversion<string>().IsRequired();
        });

        // 13. NodeRetryState Configuration
        modelBuilder.Entity<NodeRetryState>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.ExecutionInstanceId).HasConversion(executionInstanceIdConverter).IsRequired();
            entity.Property(r => r.NodeId).HasConversion(nodeIdConverter).IsRequired();
            entity.HasIndex(r => new { r.ExecutionInstanceId, r.NodeId }).IsUnique();
        });

        // 14. ScheduleFire Configuration
        modelBuilder.Entity<ScheduleFire>(entity =>
        {
            entity.HasKey(sf => sf.Id);
            entity.Property(sf => sf.Id).ValueGeneratedNever();
            entity.Property(sf => sf.ExecutionInstanceId)
                .HasConversion(new ValueConverter<ExecutionInstanceId?, Guid?>(
                    v => v.HasValue ? v.Value.Value : null,
                    v => v.HasValue ? new ExecutionInstanceId(v.Value) : null));
            entity.HasIndex(sf => new { sf.ScheduleId, sf.PlannedFireAtUtc }).IsUnique();
            entity.Property(sf => sf.Status).HasConversion<string>().IsRequired();
        });

        // 15. Schedule Configuration
        modelBuilder.Entity<Schedule>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.WorkflowDefinitionId).HasConversion(workflowIdConverter).IsRequired();
        });

        // 15b. PollingTrigger Configuration
        modelBuilder.Entity<PollingTrigger>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.WorkflowDefinitionId).HasConversion(workflowIdConverter).IsRequired();
            entity.Property(p => p.ConfigJson).IsRequired();
        });

        // 15c. AppSetting Configuration (global key/value store)
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).ValueGeneratedNever();
        });

        // 16. OpenApiSpecEntity Configuration
        modelBuilder.Entity<OpenApiSpecEntity>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).IsRequired();
            entity.Property(s => s.Title).IsRequired();
            entity.Property(s => s.ApiVersion).IsRequired();
            entity.HasMany(s => s.Versions)
                .WithOne(v => v.Spec)
                .HasForeignKey(v => v.SpecId);
        });

        // 17. OpenApiSpecVersionEntity Configuration
        modelBuilder.Entity<OpenApiSpecVersionEntity>(entity =>
        {
            entity.HasKey(v => v.RowId);
            entity.Property(v => v.RowId).ValueGeneratedOnAdd();
            entity.Property(v => v.SpecId).IsRequired();
            entity.Property(v => v.OriginalFormat).IsRequired();
            entity.Property(v => v.ParsedSpecJson).IsRequired();
            entity.HasIndex(v => new { v.SpecId, v.VersionNumber }).IsUnique();
        });

        // 18. ServerConfigEntity Configuration
        modelBuilder.Entity<ServerConfigEntity>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).IsRequired();
            entity.Property(c => c.Name).IsRequired();
            entity.Property(c => c.BaseUrl).IsRequired();
            entity.Property(c => c.ServerVariablesJson).IsRequired();
            entity.Property(c => c.SecuritySchemeType).IsRequired();
        });

        // Apply DateTimeOffset converter for SQLite compatibility (so OrderBy operates on binary values rather than text)
        var dateTimeOffsetConverter = new DateTimeOffsetToBinaryConverter();
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(dateTimeOffsetConverter);
                }
            }
        }
    }
}
