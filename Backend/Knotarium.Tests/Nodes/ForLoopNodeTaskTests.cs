// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class ForLoopNodeTaskTests
{
    private static ForLoopNodeTask Loop() => new(new InMemoryNodePackageManifestProvider());

    private static NodeExecutionContext Ctx(Dictionary<string, object> inputs, Dictionary<string, object> globals) =>
        new(WorkflowDefinitionId.New(), Guid.NewGuid(), new NodeId("loop-1"), inputs, globals);

    private static async Task<Dictionary<string, object>> RunAsync(
        ForLoopNodeTask task, Dictionary<string, object> inputs, Dictionary<string, object> globals)
    {
        var result = await task.ExecuteAsync(Ctx(inputs, globals), CancellationToken.None);
        return Assert.IsType<LegacyNodeResult.Success>(result).Outputs!;
    }

    [Fact]
    public async Task FirstRun_EntersBody_EvenWithAStrayEndProperty()
    {
        // Regression: a stray "end" value (a settable parameter whose name collides with the loop-back
        // input port) must NOT make the very first run look like a loop-back. It should initialize the
        // loop and take the body path — previously it fell straight through to "success" with 0 iterations.
        var outputs = await RunAsync(Loop(),
            new Dictionary<string, object> { ["mode"] = "count", ["count"] = 3, ["end"] = "3" },
            new Dictionary<string, object>());

        Assert.Equal("start", outputs["selectedPort"]);
        Assert.Equal(0, Convert.ToInt32(outputs["index"]));
    }

    [Fact]
    public async Task LoopsCountTimes_ThenExitsViaSuccess()
    {
        var task = Loop();
        var globals = new Dictionary<string, object>(); // shared state across iterations, as the executor does

        var first = await RunAsync(task, new Dictionary<string, object> { ["mode"] = "count", ["count"] = 2 }, globals);
        Assert.Equal("start", first["selectedPort"]);
        Assert.Equal(0, Convert.ToInt32(first["index"]));

        // Loop-back (state now exists) → index 1, still within count.
        var second = await RunAsync(task, new Dictionary<string, object> { ["mode"] = "count", ["count"] = 2, ["end"] = "x" }, globals);
        Assert.Equal("start", second["selectedPort"]);
        Assert.Equal(1, Convert.ToInt32(second["index"]));

        // Loop-back → index 2 == count → done.
        var third = await RunAsync(task, new Dictionary<string, object> { ["mode"] = "count", ["count"] = 2, ["end"] = "x" }, globals);
        Assert.Equal("success", third["selectedPort"]);
    }
}
