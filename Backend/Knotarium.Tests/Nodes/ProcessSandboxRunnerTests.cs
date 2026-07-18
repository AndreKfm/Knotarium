// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Knotarium.Features.Nodes.Sandbox;
using Xunit;

namespace Knotarium.Tests.Nodes;

// Spawns real worker processes; serialize to keep process/pipe churn deterministic and cheap.
[CollectionDefinition("ProcessSandbox", DisableParallelization = true)]
public sealed class ProcessSandboxCollection { }

/// <summary>
/// End-to-end tests for the out-of-process sandbox: real worker process, real named-pipe RPC,
/// real OS confinement. The while(true) test is the whole point of the feature — an execution
/// the in-process path can never terminate is killed at the hard deadline here.
/// </summary>
[Collection("ProcessSandbox")]
public sealed class ProcessSandboxRunnerTests : IAsyncLifetime
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubCredentialAccessor : ICredentialAccessor
    {
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(credentialRef == "ref1" ? "s3cret-value" : null);
    }

    private ProcessSandboxRunner _runner = null!;
    private Dictionary<string, object> _globals = null!;

    public Task InitializeAsync()
    {
        var options = new SandboxOptions
        {
            Mode = SandboxMode.Process,
            WorkerCount = 1,
            KillGraceSeconds = 2,
            MemoryLimitMb = 512
        };
        options.Clamp();
        _runner = new ProcessSandboxRunner(new CSharpScriptCompiler(), options,
            NullLogger<ProcessSandboxRunner>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _runner.DisposeAsync();

    private NodeExecutionContext Context()
    {
        _globals = new Dictionary<string, object>();
        return new(WorkflowDefinitionId.New(), Guid.NewGuid(), new NodeId("sbx1"),
            new Dictionary<string, object>(), _globals);
    }

    private Task<LegacyNodeResult> RunAsync(string source, int timeoutSeconds = 30, CancellationToken ct = default)
        => _runner.RunAsync(
            "sbxtest-" + Guid.NewGuid().ToString("N"), source, timeoutSeconds, Context(),
            new StubHttpClientFactory(), new StubCredentialAccessor(), NullLogger.Instance,
            extraInputs: null, knownServices: null, ct);

    [Fact]
    public async Task Simple_executor_round_trips_through_worker_process()
    {
        const string source = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Simple : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
        => new(new NodeResult(""success"", JsonSerializer.SerializeToElement(new { answer = 42 }), NodeExecutionStatus.Succeeded));
}";
        var result = await RunAsync(source);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("42", success.Outputs!["answer"]?.ToString());
    }

    [Fact]
    public async Task Infinite_loop_is_killed_at_the_hard_deadline()
    {
        // The analyzer passes this (resource exhaustion is exactly what static analysis cannot
        // catch); in-process this would spin a thread forever. The sandbox kills the process.
        const string source = @"
using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Spin : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        while (true) { } // ignores ct on purpose
    }
}";
        var result = await RunAsync(source, timeoutSeconds: 1);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("terminated", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Worker_is_replaced_after_a_kill_and_serves_the_next_run()
    {
        const string spin = @"
using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Spin : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    { while (true) { } }
}";
        const string ok = @"
using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Ok : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
        => new(new NodeResult(""success"", null, NodeExecutionStatus.Succeeded));
}";
        Assert.IsType<LegacyNodeResult.Failure>(await RunAsync(spin, timeoutSeconds: 1));

        // Pool must have retired the killed worker and spawned a fresh one for this run.
        Assert.IsType<LegacyNodeResult.Success>(await RunAsync(ok));
    }

    [Fact]
    public async Task State_secret_and_log_callbacks_flow_through_the_pipe()
    {
        const string source = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Callbacks : INodeExecutor {
    public async ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        ctx.State.SetVariable(""fromSandbox"", 42);
        var echoed = ctx.State.GetVariable<int>(""fromSandbox"");
        var secret = ctx.Credentials is null ? null : await ctx.Credentials.GetSecretAsync(""ref1"", ct);
        ctx.Logger.LogInformation(""hello from the sandbox"");
        return new NodeResult(""success"",
            JsonSerializer.SerializeToElement(new { echoed, secret }), NodeExecutionStatus.Succeeded);
    }
}";
        var result = await RunAsync(source);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        // Round-trip: worker wrote the variable into the HOST's state and read it back over the
        // pipe. Model 1 (default): the secret callback resolves — but hands the worker an opaque
        // placeholder, never the plaintext.
        Assert.Equal("42", success.Outputs!["echoed"]?.ToString());
        var secretSeen = success.Outputs!["secret"]?.ToString();
        Assert.Equal("{{knotarium-secret:ref1}}", secretSeen);
        Assert.DoesNotContain("s3cret-value", secretSeen);
        Assert.True(_globals.ContainsKey("fromSandbox"), "SetVariable must land in host workflow state");
    }

    [Fact]
    public async Task Legacy_mode_still_marshals_the_plaintext_secret()
    {
        var options = new SandboxOptions
        {
            Mode = SandboxMode.Process,
            WorkerCount = 1,
            KillGraceSeconds = 2,
            ProxyCredentials = false
        };
        options.Clamp();
        await using var legacyRunner = new ProcessSandboxRunner(
            new CSharpScriptCompiler(), options, NullLogger<ProcessSandboxRunner>.Instance);

        const string source = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class SecretReader : INodeExecutor {
    public async ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        var secret = await ctx.Credentials!.GetSecretAsync(""ref1"", ct);
        return new NodeResult(""success"", JsonSerializer.SerializeToElement(new { secret }), NodeExecutionStatus.Succeeded);
    }
}";
        var result = await legacyRunner.RunAsync(
            "sbxtest-" + Guid.NewGuid().ToString("N"), source, 30, Context(),
            new StubHttpClientFactory(), new StubCredentialAccessor(), NullLogger.Instance,
            extraInputs: null, knownServices: null, default);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("s3cret-value", success.Outputs!["secret"]?.ToString());
    }

    /// <summary>Minimal single-request HTTP server on a loopback TCP socket (HttpListener would
    /// need a URL ACL for non-admin runs). Records the Authorization header it received.</summary>
    private sealed class OneShotHttpServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly Task _serve;
        public string? SeenAuthorization { get; private set; }
        public int Port { get; }

        public OneShotHttpServer(int responseBodyBytes = 2)
        {
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    {
                        SeenAuthorization = line["Authorization:".Length..].Trim();
                    }
                }
                var body = new byte[responseBodyBytes];
                Array.Fill(body, (byte)'x');
                var header = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
            });
        }

        public async ValueTask DisposeAsync()
        {
            try { await _serve.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _listener.Stop();
        }
    }

    [Fact]
    public async Task Http_proxy_substitutes_the_placeholder_with_the_real_secret()
    {
        await using var server = new OneShotHttpServer();

        var source = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class AuthCaller : INodeExecutor {
    public async ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        var secret = await ctx.Credentials!.GetSecretAsync(""ref1"", ct);
        var req = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, ""http://127.0.0.1:" + server.Port + @"/"");
        req.Headers.TryAddWithoutValidation(""Authorization"", ""Bearer "" + secret);
        using var resp = await ctx.Http!.SendAsync(req, ct);
        return new NodeResult(""success"",
            JsonSerializer.SerializeToElement(new { status = (int)resp.StatusCode }), NodeExecutionStatus.Succeeded);
    }
}";
        var result = await RunAsync(source);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("200", success.Outputs!["status"]?.ToString());
        // The worker only held the placeholder; the wire carried the real value.
        Assert.Equal("Bearer s3cret-value", server.SeenAuthorization);
    }

    [Fact]
    public async Task Oversized_http_response_is_rejected_by_the_cap()
    {
        var options = new SandboxOptions
        {
            Mode = SandboxMode.Process,
            WorkerCount = 1,
            KillGraceSeconds = 2,
            MaxHttpResponseMb = 1
        };
        options.Clamp();
        await using var cappedRunner = new ProcessSandboxRunner(
            new CSharpScriptCompiler(), options, NullLogger<ProcessSandboxRunner>.Instance);

        await using var server = new OneShotHttpServer(responseBodyBytes: 2 * 1024 * 1024);

        var source = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class BigFetch : INodeExecutor {
    public async ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        var req = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, ""http://127.0.0.1:" + server.Port + @"/"");
        using var resp = await ctx.Http!.SendAsync(req, ct);
        return new NodeResult(""success"", null, NodeExecutionStatus.Succeeded);
    }
}";
        var result = await cappedRunner.RunAsync(
            "sbxtest-" + Guid.NewGuid().ToString("N"), source, 30, Context(),
            new StubHttpClientFactory(), new StubCredentialAccessor(), NullLogger.Instance,
            extraInputs: null, knownServices: null, default);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("exceeds", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restricted_worker_cannot_write_into_the_user_profile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // restricted-token launch is a Windows mechanism
        }

        // The banned-API analyzer would reject System.IO at compile time; disable it so the test
        // exercises the OS boundary itself — the layer that holds even when the linter is off.
        var target = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "knotarium-sbx-escape.txt");
        var source = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class ProfileWriter : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        System.IO.File.WriteAllText(@""" + target + @""", ""escaped"");
        return new(new NodeResult(""success"", null, NodeExecutionStatus.Succeeded));
    }
}";
        var originalAnalyze = CSharpScriptCompiler.EnforceBannedApiAnalysis;
        CSharpScriptCompiler.EnforceBannedApiAnalysis = false;
        try
        {
            var result = await RunAsync(source);

            // The user profile grants access to the specific user, not to the restricting SIDs
            // (Everyone/Users), so the write must be denied — and the file must not exist.
            Assert.IsType<LegacyNodeResult.Failure>(result);
            Assert.False(System.IO.File.Exists(target), "restricted worker must not reach the user profile");
        }
        finally
        {
            CSharpScriptCompiler.EnforceBannedApiAnalysis = originalAnalyze;
            if (System.IO.File.Exists(target))
            {
                System.IO.File.Delete(target);
            }
        }
    }

    [Fact]
    public async Task Sandboxed_code_cannot_spawn_child_processes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // enforced via the Job Object's active-process limit
        }

        // Process.Start via reflection: the compiler's reference set (deliberately) lacks
        // System.Diagnostics.Process, but the worker's runtime can load it — which is exactly
        // the layering this test probes: past the compiler, past the analyzer, into the Job
        // Object's active-process limit.
        var source = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Spawner : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        var t = System.Type.GetType(""System.Diagnostics.Process, System.Diagnostics.Process"", throwOnError: true);
        var start = t!.GetMethod(""Start"", new[] { typeof(string), typeof(string) });
        var child = start!.Invoke(null, new object[] { ""cmd.exe"", ""/c exit"" });
        (child as System.IDisposable)?.Dispose();
        return new(new NodeResult(""success"", null, NodeExecutionStatus.Succeeded));
    }
}";
        var originalAnalyze = CSharpScriptCompiler.EnforceBannedApiAnalysis;
        CSharpScriptCompiler.EnforceBannedApiAnalysis = false;
        try
        {
            var result = await RunAsync(source);

            // ActiveProcessLimit=1: CreateProcess inside the job fails even with the analyzer off.
            Assert.IsType<LegacyNodeResult.Failure>(result);
        }
        finally
        {
            CSharpScriptCompiler.EnforceBannedApiAnalysis = originalAnalyze;
        }
    }

    // A fresh, independent context per call — safe to use from concurrent runs (unlike the shared
    // Context() helper, which reassigns the _globals field).
    private static NodeExecutionContext FreshContext()
        => new(WorkflowDefinitionId.New(), Guid.NewGuid(), new NodeId("sbx1"),
            new Dictionary<string, object>(), new Dictionary<string, object>());

    // Reports the OS process id of the worker that served the run — the natural per-worker identity
    // used to observe pooling and recycling. Environment.ProcessId is not in the banned namespaces.
    private const string ReportPidSource = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Pid : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
        => new(new NodeResult(""success"",
            JsonSerializer.SerializeToElement(new { pid = System.Environment.ProcessId }), NodeExecutionStatus.Succeeded));
}";

    [Fact]
    public async Task Worker_is_recycled_after_the_configured_run_count()
    {
        var options = new SandboxOptions
        {
            Mode = SandboxMode.Process,
            WorkerCount = 1,
            KillGraceSeconds = 2,
            RecycleAfterRuns = 2
        };
        options.Clamp();
        await using var runner = new ProcessSandboxRunner(
            new CSharpScriptCompiler(), options, NullLogger<ProcessSandboxRunner>.Instance);

        var pids = new List<string?>();
        for (var i = 0; i < 4; i++)
        {
            var r = await runner.RunAsync(
                "recycle-pid", ReportPidSource, 30, FreshContext(),
                new StubHttpClientFactory(), new StubCredentialAccessor(), NullLogger.Instance,
                extraInputs: null, knownServices: null, default);
            pids.Add(Assert.IsType<LegacyNodeResult.Success>(r).Outputs!["pid"]?.ToString());
        }

        // WorkerCount=1, RecycleAfterRuns=2 → runs 1&2 share one worker, then it retires and
        // runs 3&4 land on a fresh one: [A, A, B, B]. The two halves must be different processes.
        Assert.Equal(pids[0], pids[1]);
        Assert.Equal(pids[2], pids[3]);
        Assert.NotEqual(pids[1], pids[2]);
    }

    [Fact]
    public async Task Pool_never_exceeds_the_configured_worker_count()
    {
        var options = new SandboxOptions
        {
            Mode = SandboxMode.Process,
            WorkerCount = 2,
            KillGraceSeconds = 2
        };
        options.Clamp();
        await using var runner = new ProcessSandboxRunner(
            new CSharpScriptCompiler(), options, NullLogger<ProcessSandboxRunner>.Instance);

        // Each run holds its worker briefly so several overlap; fire far more than the pool size.
        const string busyPid = @"
using System.Text.Json; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class BusyPid : INodeExecutor {
    public async ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        await Task.Delay(150, ct);
        return new NodeResult(""success"",
            JsonSerializer.SerializeToElement(new { pid = System.Environment.ProcessId }), NodeExecutionStatus.Succeeded);
    }
}";
        var tasks = Enumerable.Range(0, 6).Select(_ => runner.RunAsync(
            "busy-pid", busyPid, 30, FreshContext(),
            new StubHttpClientFactory(), new StubCredentialAccessor(), NullLogger.Instance,
            extraInputs: null, knownServices: null, default)).ToArray();
        var results = await Task.WhenAll(tasks);

        var pids = results
            .Select(r => Assert.IsType<LegacyNodeResult.Success>(r).Outputs!["pid"]?.ToString())
            .Distinct()
            .ToList();
        // The pool caps concurrent workers at WorkerCount, so the whole burst can only ever have
        // been served by at most that many distinct processes.
        Assert.True(pids.Count <= options.WorkerCount,
            $"pool used {pids.Count} distinct workers but the cap is {options.WorkerCount}");
    }

    [Fact]
    public async Task Worker_exceeding_the_memory_cap_fails_without_growing_unbounded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Job Object gives a deterministic hard cap; Linux enforcement needs cgroup v2 / prlimit specifics
        }

        var options = new SandboxOptions
        {
            Mode = SandboxMode.Process,
            WorkerCount = 1,
            KillGraceSeconds = 2,
            MemoryLimitMb = 512 // plenty for the worker to start; far below the 2 GB it tries to commit
        };
        options.Clamp();
        await using var runner = new ProcessSandboxRunner(
            new CSharpScriptCompiler(), options, NullLogger<ProcessSandboxRunner>.Instance);

        // Commit ~2 GB, touching every page so the pages are actually backed. The Job Object memory
        // limit denies the commit → the worker OOMs. Whether that surfaces as a caught exception or
        // a dead worker, the host must report a Failure — never let the process grow past the cap.
        const string hog = @"
using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Hog : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
    {
        var blocks = new List<byte[]>();
        for (var i = 0; i < 2048; i++)
        {
            var b = new byte[1024 * 1024];
            for (var j = 0; j < b.Length; j += 4096) { b[j] = 1; }
            blocks.Add(b);
        }
        return new(new NodeResult(""success"", null, NodeExecutionStatus.Succeeded));
    }
}";
        var result = await runner.RunAsync(
            "mem-hog", hog, 30, FreshContext(),
            new StubHttpClientFactory(), new StubCredentialAccessor(), NullLogger.Instance,
            extraInputs: null, knownServices: null, default);

        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    [Fact]
    public async Task Executor_exception_surfaces_as_failure_not_crash()
    {
        const string source = @"
using System; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Boom : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
        => throw new InvalidOperationException(""sandbox kaboom"");
}";
        var result = await RunAsync(source);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("sandbox kaboom", failure.ErrorMessage);
    }
}
