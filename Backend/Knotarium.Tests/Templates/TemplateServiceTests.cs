using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Api.Services;
using Knotarium.Features.Execution;
using Knotarium.Features.Templates;
using Knotarium.Features.Portability;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Tests.Compiler;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Knotarium.Tests.Templates;

/// <summary>Shared helpers for the template service tests.</summary>
internal static class TemplateTestFactory
{
    public static NodeDefinition N(string id, string type, params (string Key, object Value)[] props)
        => new(NodeId.Create(id), type, props.ToDictionary(p => p.Key, p => p.Value));

    public static EdgeDefinition E(string id, string from, string to)
        => new(id, NodeId.Create(from), "result", NodeId.Create(to), "in");

    /// <summary>A start→log→end graph that compiles cleanly, with the log message carrying <paramref name="logMessage"/>.</summary>
    public static WorkflowExportDocument LinearDocument(string workflowId, string name, string logMessage)
    {
        var nodes = new[]
        {
            N("start-1", "start"),
            N("log-1", "log", ("message", logMessage)),
            N("end-1", "end"),
        };
        var edges = new[] { E("e1", "start-1", "log-1"), E("e2", "log-1", "end-1") };
        var content = new WorkflowExportContent(nodes, edges);
        return new WorkflowExportDocument(
            new WorkflowExportManifest(workflowId, name, 1, "Published", null, WorkflowVersionSerializer.ComputeChecksum(content)),
            content);
    }

    public static byte[] ArchiveFrom(WorkflowExportDocument document, params TemplateCredentialSlot[] slots)
        => ArchiveWith(document, Array.Empty<TemplateParameter>(), slots);

    public static byte[] ArchiveWith(
        WorkflowExportDocument document,
        IReadOnlyList<TemplateParameter> parameters,
        params TemplateCredentialSlot[] slots)
    {
        var manifest = new TemplateManifest(
            "tpl_" + document.Manifest.WorkflowId, "1.0.0", TemplateFormat.SchemaVersion,
            document.Manifest.WorkflowName, "author", "desc", new[] { "t" }, "cat", null,
            "2026-01-01T00:00:00.0000000Z", document.Manifest.WorkflowName,
            document.Manifest.Checksum, slots)
        {
            Parameters = parameters,
        };
        return TemplateArchiveCodec.Write(new TemplateArchive(manifest, WorkflowVersionSerializer.Serialize(document)));
    }

    /// <summary>A start→log→end graph whose log node carries both a <c>{{param:greeting}}</c> token and a
    /// <c>slot:api-key</c> placeholder — exercises the params-then-slots install path.</summary>
    public static WorkflowExportDocument ParamAndSlotDocument()
    {
        var nodes = new[]
        {
            N("start-1", "start"),
            N("log-1", "log", ("message", "{{param:greeting}}"), ("apiKey", "slot:api-key")),
            N("end-1", "end"),
        };
        var edges = new[] { E("e1", "start-1", "log-1"), E("e2", "log-1", "end-1") };
        var content = new WorkflowExportContent(nodes, edges);
        return new WorkflowExportDocument(
            new WorkflowExportManifest("src-wf", "Flow", 1, "Published", null, WorkflowVersionSerializer.ComputeChecksum(content)),
            content);
    }

    public static TemplateParameter RequiredString(string key) => new(key, key, null, "string", null, null, true);

    public static string? PropText(WorkflowExportContent content, string nodeId, string key)
    {
        var value = content.Nodes.Single(n => n.Id.Value == nodeId).Properties[key];
        return value is System.Text.Json.JsonElement element ? element.GetString() : value?.ToString();
    }

    public static WorkflowCompiler Compiler() =>
        new(new MockWorkflowDefinitionProvider(), new InMemoryNodePackageManifestProvider());
}

public class TemplateExportServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public TemplateExportServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private sealed class FakeSource(PublishedWorkflow? published) : IPublishedWorkflowExportSource
    {
        public Task<PublishedWorkflow?> GetAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default)
            => Task.FromResult(published);
    }

    private static WorkflowVersion VersionReferencing(string workflowId, string credentialId)
    {
        var node = new NodeDefinition(
            NodeId.Create("http-1"), "httpRequest", new Dictionary<string, object> { ["credential"] = credentialId });
        return new WorkflowVersion(
            WorkflowVersionId.New(), new WorkflowDefinitionId(workflowId), 1,
            new[] { node }, Array.Empty<EdgeDefinition>(), DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Export_portabilizes_credentials_and_round_trips()
    {
        await using var context = await CreateContextAsync();
        context.Credentials.Add(new Credential { Id = "cred-1", Name = "Weather API", EncryptedValue = "enc", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
        await context.SaveChangesAsync();

        var source = new FakeSource(new PublishedWorkflow(VersionReferencing("wf-1", "cred-1"), "My Flow"));
        var service = new TemplateExportService(source, context, TimeProvider.System);

        var result = await service.ExportAsync(new TemplateExportRequest("wf-1", Name: "Weather Template", Author: "me"));

        Assert.NotNull(result);
        var slot = Assert.Single(result!.Manifest.CredentialSlots);
        Assert.Equal("weather-api", slot.Slot);
        Assert.Contains("http-1.credential", result.Report.RewrittenPaths);
        Assert.Equal("Weather Template", result.Manifest.Name);

        // The shipped bytes must round-trip and never carry the host credential id.
        var archive = TemplateArchiveCodec.Read(result.Bytes);
        TemplateWorkflowReader.ReadAndVerify(archive);
        Assert.DoesNotContain("cred-1", archive.WorkflowDocumentJson);
        Assert.Contains("slot:weather-api", archive.WorkflowDocumentJson);
    }

    [Fact]
    public async Task Export_returns_null_when_workflow_has_no_version()
    {
        await using var context = await CreateContextAsync();
        var service = new TemplateExportService(new FakeSource(null), context, TimeProvider.System);

        var result = await service.ExportAsync(new TemplateExportRequest("missing"));

        Assert.Null(result);
    }

    [Fact]
    public async Task Export_rejects_invalid_template_version()
    {
        await using var context = await CreateContextAsync();
        var source = new FakeSource(new PublishedWorkflow(VersionReferencing("wf-1", "cred-1"), "Flow"));
        var service = new TemplateExportService(source, context, TimeProvider.System);

        await Assert.ThrowsAsync<TemplateExportException>(
            () => service.ExportAsync(new TemplateExportRequest("wf-1", TemplateVersion: "not-semver")));
    }
}

public class TemplateInspectServiceTests
{
    [Fact]
    public async Task Inspect_reports_slots_and_supported_for_a_clean_workflow()
    {
        var doc = TemplateTestFactory.LinearDocument("wf-1", "Flow", "slot:api-key");
        var bytes = TemplateTestFactory.ArchiveFrom(doc, new TemplateCredentialSlot("api-key", "API Key", null, null));
        var service = new TemplateInspectService(new TemplateCompatibilityChecker(TemplateTestFactory.Compiler()), new InMemoryNodePackageManifestProvider());

        var result = await service.InspectAsync(bytes);

        Assert.True(result.Compatibility.Supported);
        Assert.Equal("api-key", Assert.Single(result.CredentialSlots).Slot);
    }

    [Fact]
    public async Task Inspect_flags_unknown_node_types_as_unsupported()
    {
        var nodes = new[] { TemplateTestFactory.N("ghost-1", "ghostNode") };
        var content = new WorkflowExportContent(nodes, Array.Empty<EdgeDefinition>());
        var doc = new WorkflowExportDocument(
            new WorkflowExportManifest("wf-2", "Ghost", 1, "Published", null, WorkflowVersionSerializer.ComputeChecksum(content)),
            content);
        var bytes = TemplateTestFactory.ArchiveFrom(doc);
        var service = new TemplateInspectService(new TemplateCompatibilityChecker(TemplateTestFactory.Compiler()), new InMemoryNodePackageManifestProvider());

        var result = await service.InspectAsync(bytes);

        Assert.False(result.Compatibility.Supported);
        Assert.NotEmpty(result.Compatibility.Warnings);
    }

    [Fact]
    public async Task Inspect_reports_privileged_nodes()
    {
        var nodes = new[]
        {
            TemplateTestFactory.N("start-1", "start"),
            TemplateTestFactory.N("write-1", "fileWrite"),
            TemplateTestFactory.N("code-1", "inlineCode"),
        };
        var content = new WorkflowExportContent(nodes, Array.Empty<EdgeDefinition>());
        var doc = new WorkflowExportDocument(
            new WorkflowExportManifest("wf-priv", "Privileged", 1, "Published", null, WorkflowVersionSerializer.ComputeChecksum(content)),
            content);
        var bytes = TemplateTestFactory.ArchiveFrom(doc);
        var service = new TemplateInspectService(new TemplateCompatibilityChecker(TemplateTestFactory.Compiler()), new InMemoryNodePackageManifestProvider());

        var result = await service.InspectAsync(bytes);

        var types = result.PrivilegedNodes.Select(p => p.NodeType).ToHashSet();
        Assert.Contains("fileWrite", types);
        Assert.Contains("inlineCode", types);
        Assert.DoesNotContain("start", types);
    }
}

public class TemplatePayloadServiceTests
{
    [Fact]
    public async Task Payload_returns_the_graph_and_compatibility_without_installing()
    {
        var doc = TemplateTestFactory.LinearDocument("src-wf", "Flow", "slot:api-key");
        var bytes = TemplateTestFactory.ArchiveFrom(doc, new TemplateCredentialSlot("api-key", "API Key", null, null));
        var service = new TemplatePayloadService(new TemplateCompatibilityChecker(TemplateTestFactory.Compiler()));

        var payload = await service.GetPayloadAsync(bytes);

        Assert.True(payload.Compatibility.Supported);
        Assert.Equal(3, payload.Content.Nodes.Count); // start → log → end
        Assert.Equal(2, payload.Content.Edges.Count);
        Assert.Equal("api-key", Assert.Single(payload.CredentialSlots).Slot);
    }

    [Fact]
    public async Task Payload_substitutes_parameters_but_leaves_credential_slots()
    {
        var doc = TemplateTestFactory.ParamAndSlotDocument();
        var bytes = TemplateTestFactory.ArchiveWith(
            doc, new[] { TemplateTestFactory.RequiredString("greeting") },
            new TemplateCredentialSlot("api-key", "API Key", null, null));
        var service = new TemplatePayloadService(new TemplateCompatibilityChecker(TemplateTestFactory.Compiler()));

        var payload = await service.GetPayloadAsync(bytes, new Dictionary<string, string> { ["greeting"] = "Hi there" });

        Assert.Equal("Hi there", TemplateTestFactory.PropText(payload.Content, "log-1", "message"));
        Assert.Equal("slot:api-key", TemplateTestFactory.PropText(payload.Content, "log-1", "apiKey"));
    }
}

public class TemplateInstallServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public TemplateInstallServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private sealed class FakeImporter : ITemplateWorkflowImporter
    {
        public List<WorkflowExportDocument> Imported { get; } = new();

        public Task<int> ImportAsync(WorkflowExportDocument document, CancellationToken cancellationToken = default)
        {
            Imported.Add(document);
            return Task.FromResult(Imported.Count);
        }
    }

    // Minimal IWorkflowStore — only ListAsync (for name-collision resolution) is exercised here.
    private sealed class FakeWorkflowStore(params string[] names) : Knotarium.Core.Contracts.IWorkflowStore
    {
        public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowDefinition>>(
                names.Select(n => new WorkflowDefinition(new WorkflowDefinitionId(Guid.NewGuid().ToString("N")), n, Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>())).ToList());

        public Task<WorkflowDefinition?> GetAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default) => Task.FromResult<WorkflowDefinition?>(null);
        public Task<WorkflowDefinition> UpsertAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default) => Task.FromResult(workflow);
        public Task<WorkflowDefinition?> UpdateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default) => Task.FromResult<WorkflowDefinition?>(workflow);
        public Task<bool> DeleteAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private TemplateInstallService Service(AppDbContext context, FakeImporter importer, params string[] existingNames) =>
        new(context, importer, new TemplateCompatibilityChecker(TemplateTestFactory.Compiler()), new FakeWorkflowStore(existingNames));

    [Fact]
    public async Task Install_binds_slot_and_imports_under_a_fresh_workflow_id()
    {
        await using var context = await CreateContextAsync();
        context.Credentials.Add(new Credential { Id = "cred-live", Name = "Live", EncryptedValue = "e", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
        await context.SaveChangesAsync();

        var doc = TemplateTestFactory.LinearDocument("src-wf", "Flow", "slot:api-key");
        var bytes = TemplateTestFactory.ArchiveFrom(doc, new TemplateCredentialSlot("api-key", "API Key", null, null));
        var importer = new FakeImporter();

        var result = await Service(context, importer).InstallAsync(
            bytes, new Dictionary<string, string> { ["api-key"] = "cred-live" });

        Assert.Equal(new[] { "api-key" }, result.ReboundSlots);
        Assert.Empty(result.OpenSlots);
        Assert.False(result.ConfigurationRequired);
        Assert.True(result.Runnable);

        var imported = Assert.Single(importer.Imported);
        Assert.NotEqual("src-wf", imported.Manifest.WorkflowId); // a fresh id, never clobbering the source
        Assert.Equal(result.WorkflowId, imported.Manifest.WorkflowId);
        Assert.DoesNotContain(imported.Content.Nodes, n => n.Properties.Values.Any(v => v.ToString() == "slot:api-key"));
    }

    [Fact]
    public async Task Install_with_missing_binding_succeeds_as_configuration_required()
    {
        await using var context = await CreateContextAsync();
        var doc = TemplateTestFactory.LinearDocument("src-wf", "Flow", "slot:api-key");
        var bytes = TemplateTestFactory.ArchiveFrom(doc, new TemplateCredentialSlot("api-key", "API Key", null, null));
        var importer = new FakeImporter();

        var result = await Service(context, importer).InstallAsync(bytes, credentialBindings: null);

        Assert.Equal(new[] { "api-key" }, result.OpenSlots);
        Assert.True(result.ConfigurationRequired);
        Assert.False(result.Runnable);
        Assert.Single(importer.Imported); // still imported as a draft
    }

    [Fact]
    public async Task Install_rejects_unknown_binding_key()
    {
        await using var context = await CreateContextAsync();
        context.Credentials.Add(new Credential { Id = "cred-live", Name = "Live", EncryptedValue = "e", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
        await context.SaveChangesAsync();

        var doc = TemplateTestFactory.LinearDocument("src-wf", "Flow", "slot:api-key");
        var bytes = TemplateTestFactory.ArchiveFrom(doc, new TemplateCredentialSlot("api-key", "API Key", null, null));

        await Assert.ThrowsAsync<TemplateBindingException>(() =>
            Service(context, new FakeImporter()).InstallAsync(
                bytes, new Dictionary<string, string> { ["does-not-exist"] = "cred-live" }));
    }

    [Fact]
    public async Task Install_rejects_binding_to_nonexistent_credential()
    {
        await using var context = await CreateContextAsync();
        var doc = TemplateTestFactory.LinearDocument("src-wf", "Flow", "slot:api-key");
        var bytes = TemplateTestFactory.ArchiveFrom(doc, new TemplateCredentialSlot("api-key", "API Key", null, null));

        var ex = await Assert.ThrowsAsync<TemplateBindingException>(() =>
            Service(context, new FakeImporter()).InstallAsync(
                bytes, new Dictionary<string, string> { ["api-key"] = "ghost-credential" }));
        Assert.Contains(ex.Errors, message => message.Contains("ghost-credential", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_honors_an_explicit_workflow_name()
    {
        await using var context = await CreateContextAsync();
        var doc = TemplateTestFactory.LinearDocument("src-wf", "Template Default Name", "hi");
        var bytes = TemplateTestFactory.ArchiveFrom(doc);
        var importer = new FakeImporter();

        var result = await Service(context, importer).InstallAsync(bytes, credentialBindings: null, workflowName: "My Renamed Copy");

        Assert.Equal("My Renamed Copy", result.WorkflowName);
        Assert.Equal("My Renamed Copy", Assert.Single(importer.Imported).Manifest.WorkflowName);
    }

    [Fact]
    public async Task Install_substitutes_parameters_then_rebinds_slots()
    {
        await using var context = await CreateContextAsync();
        context.Credentials.Add(new Credential { Id = "cred-live", Name = "Live", EncryptedValue = "e", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
        await context.SaveChangesAsync();

        var doc = TemplateTestFactory.ParamAndSlotDocument();
        var bytes = TemplateTestFactory.ArchiveWith(
            doc, new[] { TemplateTestFactory.RequiredString("greeting") },
            new TemplateCredentialSlot("api-key", "API Key", null, null));
        var importer = new FakeImporter();

        var result = await Service(context, importer).InstallAsync(
            bytes,
            new Dictionary<string, string> { ["api-key"] = "cred-live" },
            parameterValues: new Dictionary<string, string> { ["greeting"] = "Hello world" });

        Assert.Equal(new[] { "api-key" }, result.ReboundSlots);
        var imported = Assert.Single(importer.Imported);
        Assert.Equal("Hello world", TemplateTestFactory.PropText(imported.Content, "log-1", "message")); // param substituted
        Assert.Equal("cred-live", TemplateTestFactory.PropText(imported.Content, "log-1", "apiKey"));     // slot rebound
    }

    [Fact]
    public async Task Install_rejects_a_graph_token_for_an_undeclared_parameter()
    {
        await using var context = await CreateContextAsync();
        context.Credentials.Add(new Credential { Id = "cred-live", Name = "Live", EncryptedValue = "e", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
        await context.SaveChangesAsync();

        // The graph carries {{param:greeting}}, but the manifest declares NO parameters — a residual token
        // would otherwise be persisted literally. Install must fail.
        var doc = TemplateTestFactory.ParamAndSlotDocument();
        var bytes = TemplateTestFactory.ArchiveWith(
            doc, Array.Empty<TemplateParameter>(), new TemplateCredentialSlot("api-key", "API Key", null, null));

        await Assert.ThrowsAsync<TemplateParameterException>(() =>
            Service(context, new FakeImporter()).InstallAsync(
                bytes, new Dictionary<string, string> { ["api-key"] = "cred-live" }));
    }

    [Fact]
    public async Task Install_rejects_a_missing_required_parameter()
    {
        await using var context = await CreateContextAsync();
        var doc = TemplateTestFactory.ParamAndSlotDocument();
        var bytes = TemplateTestFactory.ArchiveWith(
            doc, new[] { TemplateTestFactory.RequiredString("greeting") },
            new TemplateCredentialSlot("api-key", "API Key", null, null));

        await Assert.ThrowsAsync<TemplateParameterException>(() =>
            Service(context, new FakeImporter()).InstallAsync(bytes, credentialBindings: null, parameterValues: null));
    }

    [Fact]
    public async Task Install_suffixes_the_name_on_collision()
    {
        await using var context = await CreateContextAsync();
        var doc = TemplateTestFactory.LinearDocument("src-wf", "Flow", "hi");
        var bytes = TemplateTestFactory.ArchiveFrom(doc);
        var importer = new FakeImporter();

        // A workflow named "Flow" already exists → the import becomes "Flow (2)".
        var result = await Service(context, importer, "Flow").InstallAsync(bytes, credentialBindings: null);

        Assert.Equal("Flow (2)", result.WorkflowName);
    }
}
