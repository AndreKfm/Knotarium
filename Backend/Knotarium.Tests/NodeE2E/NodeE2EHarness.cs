// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Options;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Features.Execution;
using Knotarium.Features.Options;
using Knotarium.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Knotarium.Tests.NodeE2E;

/// <summary>
/// End-to-end harness that runs the <b>real</b> built-in node tasks through the <b>real</b>
/// <see cref="WorkflowExecutor"/> against a temp-file SQLite database. Unlike
/// <c>ExecutionEngineTests</c> (which registers mock <c>INodeTask</c> stubs), this stands up the actual
/// DI graph via <c>AddBuiltInNodes()</c> so a passing test proves the shipped node code executes,
/// produces the expected output ports, and routes correctly when driven by the engine.
///
/// The only things faked are the true external edges — outbound HTTP (a stub handler on the "HttpNode"
/// named client), secret resolution, and the notification store/dispatcher. Capability and file-access
/// policy use the real guards fed by permissive-but-explicit test policies. Everything else (compiler,
/// journal writer, node-task registry, node tasks) is production code.
/// </summary>
public sealed class NodeE2EHarness : IDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly ServiceProvider _services;

    private readonly StubSecretResolver _secrets = new();
    private readonly MutableCapabilityPolicy _capabilities = new();
    private readonly StubHttpHandler _httpHandler = new();
    private readonly StubChatCompletionService _chat = new();
    private readonly StubNotificationChannelStore _channels = new();
    private readonly RecordingNotificationDispatcher _dispatcher = new();
    private readonly List<OptionItem> _resources = new();

    /// <summary>An absolute directory the file nodes are permitted to read/write during the test.</summary>
    public string WorkDir { get; }

    public NodeE2EHarness()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-nodee2e-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_databasePath}";
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

        WorkDir = Path.Combine(Path.GetTempPath(), $"knotarium-nodee2e-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(WorkDir);

        using (var seed = new AppDbContext(_dbOptions))
        {
            seed.Database.EnsureCreated();
        }

        var services = new ServiceCollection();
        services.AddLogging();

        // Register the permissive test policies BEFORE AddBuiltInNodes so its TryAdd secure-defaults
        // (deny-all file access, all capabilities off, not-configured chat completion) do not win.
        services.AddSingleton<IFileAccessPolicyProvider>(new PermissiveFileAccessPolicyProvider(WorkDir));
        services.AddSingleton<ICapabilityPolicy>(_capabilities);
        services.AddSingleton<Knotarium.Core.Contracts.Ai.IChatCompletionService>(_chat);

        services.AddBuiltInNodes();

        // ForLoopNodeTask injects the concrete provider; the compiler uses the interface. Same instance.
        services.AddSingleton<InMemoryNodePackageManifestProvider>();
        services.AddSingleton<INodePackageManifestProvider>(sp => sp.GetRequiredService<InMemoryNodePackageManifestProvider>());

        // External edges — faked.
        services.AddSingleton<ISecretResolver>(_secrets);
        services.AddSingleton<ICredentialAccessor>(_secrets);
        services.AddSingleton<INotificationChannelStore>(_channels);
        services.AddSingleton<INotificationDispatcher>(_dispatcher);

        // The ResourcePicker node resolves selected options via ResourceResolver over a loader registry;
        // register the real resolver fed by a stub loader that returns the harness-configured resources.
        services.AddSingleton<IOptionsLoaderRegistry>(new StubOptionsLoaderRegistry(_resources));
        services.AddSingleton<ResourceResolver>();

        // The HttpRequest node resolves the named client "HttpNode"; route it through the stub handler.
        services.AddHttpClient("HttpNode").ConfigurePrimaryHttpMessageHandler(() => _httpHandler);

        _services = services.BuildServiceProvider();
    }

    // --- Seam configuration (call before RunNodeAsync) ---

    /// <summary>Enable a capability tag (e.g. <c>code.execute</c>, <c>database</c>) for capability-gated nodes.</summary>
    public NodeE2EHarness EnableCapability(string capability)
    {
        _capabilities.Enable(capability);
        return this;
    }

    /// <summary>Register a secret/credential value resolvable by <c>connectionRef</c> / <c>credentialRef</c>.</summary>
    public NodeE2EHarness WithSecret(string reference, string value)
    {
        _secrets.Set(reference, value);
        return this;
    }

    /// <summary>Set the canned HTTP response the stubbed "HttpNode" client returns for every request.</summary>
    public NodeE2EHarness WithHttpResponse(HttpStatusCode status, string body)
    {
        _httpHandler.Configure(status, body);
        return this;
    }

    /// <summary>Set the canned reply the stubbed chat-completion service returns to the AI prompt node.</summary>
    public NodeE2EHarness WithChatReply(string reply)
    {
        _chat.Configure(reply);
        return this;
    }

    /// <summary>Chat-completion requests the AI prompt node issued, for assertions.</summary>
    public IReadOnlyList<Knotarium.Core.Contracts.Ai.ChatCompletionRequest> ChatRequests => _chat.Requests;

    /// <summary>Register a notification channel the Send Notification node can resolve.</summary>
    public NodeE2EHarness WithNotificationChannel(NotificationChannel channel)
    {
        _channels.Add(channel);
        return this;
    }

    /// <summary>Register a selectable resource (value + label) the ResourcePicker node can resolve.</summary>
    public NodeE2EHarness WithResource(string value, string label)
    {
        _resources.Add(new OptionItem(label, value));
        return this;
    }

    /// <summary>Notification messages captured by the fake dispatcher, for assertions.</summary>
    public IReadOnlyList<(NotificationChannel Channel, NotificationMessage Message)> SentNotifications => _dispatcher.Sent;

    // --- Execution ---

    /// <summary>
    /// Run a single node in a <c>start → node → end</c> workflow and return the result. The
    /// <paramref name="outputPort"/> is the manifest output port that carries the node's success value to
    /// <c>end</c> ("result" for most nodes; "success" for httpRequest; "true"/"false" for condition; etc.).
    /// </summary>
    public Task<NodeRunResult> RunNodeAsync(
        string nodeType,
        Dictionary<string, object>? config = null,
        string outputPort = "result")
    {
        var node = new NodeDefinition(NodeId.Create("node-1"), nodeType, config ?? new Dictionary<string, object>());
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e-start", start.Id, "result", node.Id, "in"),
            new EdgeDefinition("e-end", node.Id, outputPort, end.Id, "in"),
        };

        return RunWorkflowAsync(new[] { start, node, end }, edges);
    }

    /// <summary>Run an arbitrary node/edge graph and return the persisted per-node result.</summary>
    public async Task<NodeRunResult> RunWorkflowAsync(
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges,
        string triggerOrigin = "manual",
        string? resumeEventName = null,
        Dictionary<string, object>? resumeEventData = null)
    {
        var workflowId = WorkflowDefinitionId.New();
        var instanceId = ExecutionInstanceId.New();

        await using (var context = new AppDbContext(_dbOptions))
        {
            var definition = new WorkflowDefinition(workflowId, "NodeE2E", nodes, edges);
            // A real run always has a pinned version; the timed-Delay suspend path (and resume work items)
            // reference instance.WorkflowVersionId, so create one rather than leaving it null.
            var version = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, definition.Nodes, definition.Edges, DateTimeOffset.UtcNow);
            context.WorkflowDefinitions.Add(definition);
            context.WorkflowVersions.Add(version);
            context.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = instanceId,
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = version.Id,
                Status = ExecutionStatus.Pending,
                TriggerOrigin = triggerOrigin,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await ExecuteAsync(instanceId, resumeEventName, resumeEventData);

        return await ReadResultAsync(instanceId);
    }

    /// <summary>Resume a suspended run (e.g. a Delay ≥ 1s that returned WaitForEvent) and re-read the result.</summary>
    public async Task<NodeRunResult> ResumeAsync(
        ExecutionInstanceId instanceId,
        string eventName,
        Dictionary<string, object> eventData)
    {
        await ExecuteAsync(instanceId, eventName, eventData);
        return await ReadResultAsync(instanceId);
    }

    private async Task ExecuteAsync(
        ExecutionInstanceId instanceId,
        string? resumeEventName,
        Dictionary<string, object>? resumeEventData)
    {
        // Fresh scope + context per invocation: the registry resolves node tasks (transient) from the
        // scope, and the executor tracks state on its own context, exactly as a real run would.
        using var scope = _services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<INodeTaskRegistry>();
        var manifestProvider = scope.ServiceProvider.GetRequiredService<INodePackageManifestProvider>();

        await using var context = new AppDbContext(_dbOptions);
        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connectionString);
        var executor = new WorkflowExecutor(context, compiler, registry, new NullEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId, resumeEventName, resumeEventData);
    }

    private async Task<NodeRunResult> ReadResultAsync(ExecutionInstanceId instanceId)
    {
        await using var context = new AppDbContext(_dbOptions);
        var instance = await context.ExecutionInstances
            .Include(e => e.NodeStates)
            .Include(e => e.JournalEntries)
            .SingleAsync(e => e.Id == instanceId);

        return new NodeRunResult(
            instanceId,
            instance.Status,
            instance.NodeStates.ToDictionary(s => s.NodeId.Value, s => s),
            instance.JournalEntries.OrderBy(j => j.Timestamp).ToList(),
            instance.GlobalVariables);
    }

    public void Dispose()
    {
        _services.Dispose();
        _httpHandler.Dispose();
        TryDelete(() => File.Delete(_databasePath));
        TryDelete(() => Directory.Delete(WorkDir, recursive: true));
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // --- Fakes ---

    private sealed class NullEventPublisher : IExecutionEventPublisher
    {
        public Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class MutableCapabilityPolicy : ICapabilityPolicy
    {
        private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
        public void Enable(string capability) => _enabled.Add(capability);
        public Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default)
            => Task.FromResult(_enabled.Contains(capability));
    }

    private sealed class StubSecretResolver : ISecretResolver, ICredentialAccessor
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
        public void Set(string reference, string value) => _secrets[reference] = value;
        public Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.TryGetValue(secretRef, out var v) ? v : null);
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
            => ResolveAsync(credentialRef, cancellationToken);
    }

    private sealed class StubChatCompletionService : Knotarium.Core.Contracts.Ai.IChatCompletionService
    {
        private string _reply = "stub reply";
        public List<Knotarium.Core.Contracts.Ai.ChatCompletionRequest> Requests { get; } = new();
        public void Configure(string reply) => _reply = reply;
        public Task<string> CompleteAsync(Knotarium.Core.Contracts.Ai.ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_reply);
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";
        public void Configure(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    private sealed class StubNotificationChannelStore : INotificationChannelStore
    {
        private readonly Dictionary<string, NotificationChannel> _channels = new(StringComparer.Ordinal);
        public void Add(NotificationChannel channel) => _channels[channel.Id] = channel;
        public Task<NotificationChannel?> GetAsync(string channelId, CancellationToken cancellationToken = default)
            => Task.FromResult(_channels.TryGetValue(channelId, out var c) ? c : null);
        public Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationChannel>>(_channels.Values.ToList());
    }

    private sealed class RecordingNotificationDispatcher : INotificationDispatcher
    {
        public List<(NotificationChannel, NotificationMessage)> Sent { get; } = new();
        public Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken cancellationToken)
        {
            Sent.Add((channel, message));
            return Task.CompletedTask;
        }
    }

    private sealed class StubOptionsLoaderRegistry : IOptionsLoaderRegistry
    {
        private readonly StubOptionsLoader _loader;
        public StubOptionsLoaderRegistry(IReadOnlyList<OptionItem> items) => _loader = new StubOptionsLoader(items);
        public IOptionsLoader? Get(string name) => name == _loader.Name ? _loader : null;
    }

    private sealed class StubOptionsLoader : IOptionsLoader
    {
        private readonly IReadOnlyList<OptionItem> _items;
        public StubOptionsLoader(IReadOnlyList<OptionItem> items) => _items = items;
        public string Name => RestCollectionOptionsLoader.LoaderName;
        public Task<OptionListResult> LoadAsync(OptionLoadContext context, CancellationToken cancellationToken)
            => Task.FromResult(new OptionListResult(_items.ToList()));
    }

    private sealed class PermissiveFileAccessPolicyProvider : IFileAccessPolicyProvider
    {
        private readonly FileAccessPolicy _policy;
        public PermissiveFileAccessPolicyProvider(string allowedDir)
            => _policy = new FileAccessPolicy(
                TotalAccess: false,
                Rules: new[] { new FileAccessRule(allowedDir, FileAccessMode.ReadWrite) },
                MinFreeBytes: null,
                MinFreePercent: null);
        public Task<FileAccessPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_policy);
    }
}

/// <summary>The persisted outcome of a harness run.</summary>
public sealed record NodeRunResult(
    ExecutionInstanceId InstanceId,
    ExecutionStatus Status,
    IReadOnlyDictionary<string, NodeState> NodeStates,
    IReadOnlyList<ExecutionJournal> Journal,
    IReadOnlyDictionary<string, object> GlobalVariables)
{
    /// <summary>The state of the node under test in a <c>RunNodeAsync</c> run.</summary>
    public NodeState Node => NodeStates["node-1"];

    public NodeState State(string nodeId) => NodeStates[nodeId];

    public bool Ran(string nodeId) => NodeStates.ContainsKey(nodeId);
}
