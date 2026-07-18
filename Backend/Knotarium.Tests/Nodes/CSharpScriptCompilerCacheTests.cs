// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

// The compiler's compiled-type cache is process-static, so these tests share it. Serialize them (and
// keep the eviction test's global-cap mutation from racing another cache test) with a non-parallel
// collection.
[CollectionDefinition("ScriptCompilerCache", DisableParallelization = true)]
public sealed class ScriptCompilerCacheCollection { }

[Collection("ScriptCompilerCache")]
public sealed class CSharpScriptCompilerCacheTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubCredentialAccessor : ICredentialAccessor
    {
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private static NodeExecutionContext Context() => new(
        WorkflowDefinitionId.New(), Guid.NewGuid(), new NodeId("compiler-test"),
        new Dictionary<string, object>(), new Dictionary<string, object>());

    [Fact]
    public void Same_key_compiles_once_under_concurrency()
    {
        var compiler = new CSharpScriptCompiler();
        const string key = "concurrency-single-compile";
        const string source = "var x = 41 + 1;";

        var types = new ConcurrentBag<Type>();
        Parallel.For(0, 16, _ => types.Add(compiler.GetOrCompile(key, source)));

        // One shared Type instance => the source compiled exactly once; no racing thread built and then
        // orphaned a second collectible load context.
        Assert.Single(types.Distinct());
    }

    [Fact]
    public void Failed_compile_is_not_cached_and_can_retry()
    {
        var compiler = new CSharpScriptCompiler();
        var badKey = "bad-" + Guid.NewGuid().ToString("N");

        Assert.Throws<ScriptCompilationException>(() => compiler.GetOrCompile(badKey, "this is not valid c# @@@"));
        // Not poisoned: the second attempt re-runs the compile (and fails again) rather than returning a
        // cached success or a cached exception.
        Assert.Throws<ScriptCompilationException>(() => compiler.GetOrCompile(badKey, "this is not valid c# @@@"));
        Assert.False(CSharpScriptCompiler.ContainsCompiledKey(badKey));

        // A good compile under a different key still works after the failures.
        Assert.NotNull(compiler.GetOrCompile("good-" + Guid.NewGuid().ToString("N"), "var ok = 1;"));
    }

    [Fact]
    public void Cache_is_bounded_and_evicts_the_oldest_entry()
    {
        var compiler = new CSharpScriptCompiler();
        var original = CSharpScriptCompiler.MaxCachedTypes;
        try
        {
            CSharpScriptCompiler.MaxCachedTypes = 8;

            var firstKey = "evict-first-" + Guid.NewGuid().ToString("N");
            compiler.GetOrCompile(firstKey, "var v = 0;");
            for (var i = 1; i < 40; i++)
                compiler.GetOrCompile("evict-" + Guid.NewGuid().ToString("N"), "var v = " + i + ";");

            // The oldest entry (lowest compile sequence) is evicted, and the cache stays bounded — without
            // eviction it would hold all 40 distinct keys.
            Assert.False(CSharpScriptCompiler.ContainsCompiledKey(firstKey));
            Assert.True(CSharpScriptCompiler.CachedTypeCount < 40);
        }
        finally
        {
            CSharpScriptCompiler.MaxCachedTypes = original;
        }
    }

    [Fact]
    public async Task RunAsync_normalizes_a_throwing_full_executor_into_a_failure()
    {
        var compiler = new CSharpScriptCompiler();
        const string throwingExecutor = @"
using System; using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Boom : INodeExecutor {
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext context, CancellationToken ct)
        => throw new InvalidOperationException(""kaboom"");
}";
        var type = compiler.GetOrCompile("throwing-" + Guid.NewGuid().ToString("N"), throwingExecutor);
        var executor = compiler.Instantiate(type);

        // A full executor (unlike a wrapped inline script) has no internal try/catch, so its exception
        // escapes ExecuteAsync — RunAsync must normalize it to a Failure, not let it propagate raw.
        var result = await compiler.RunAsync(
            executor, Context(), new StubHttpClientFactory(), new StubCredentialAccessor(), NullLogger.Instance);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("kaboom", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // A full executor that always compiles but is rejected by BannedApiAnalyzer (static mutable state).
    private const string StatefulExecutor = @"
using System.Threading; using System.Threading.Tasks;
using Knotarium.Core.Contracts; using Knotarium.Core.Domain;
public class Stateful : INodeExecutor {
    static int Counter = 0;
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext context, CancellationToken ct)
        => default;
}";

    [Fact]
    public void Runtime_compile_rejects_banned_source_when_screening_enabled()
    {
        var compiler = new CSharpScriptCompiler();
        var original = CSharpScriptCompiler.EnforceBannedApiAnalysis;
        try
        {
            CSharpScriptCompiler.EnforceBannedApiAnalysis = true;

            var ex = Assert.Throws<ScriptCompilationException>(
                () => compiler.GetOrCompile("banned-on-" + Guid.NewGuid().ToString("N"), StatefulExecutor));

            // The runtime path now applies the same gate as the editor — screening happens before compile.
            Assert.Contains("security analysis", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CSharpScriptCompiler.EnforceBannedApiAnalysis = original;
        }
    }

    [Fact]
    public void Runtime_compile_allows_banned_source_when_screening_disabled()
    {
        var compiler = new CSharpScriptCompiler();
        var original = CSharpScriptCompiler.EnforceBannedApiAnalysis;
        try
        {
            CSharpScriptCompiler.EnforceBannedApiAnalysis = false;

            // Same source that was rejected above compiles cleanly once the operator opts out of screening,
            // proving the gate is the only thing rejecting it (the source itself is valid C#).
            var type = compiler.GetOrCompile("banned-off-" + Guid.NewGuid().ToString("N"), StatefulExecutor);
            Assert.NotNull(type);
        }
        finally
        {
            CSharpScriptCompiler.EnforceBannedApiAnalysis = original;
        }
    }
}
