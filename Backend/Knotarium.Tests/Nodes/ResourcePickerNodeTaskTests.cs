using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Options;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Knotarium.Features.Options;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class ResourcePickerNodeTaskTests
{
    private sealed class StubLoader : IOptionsLoader
    {
        private readonly List<OptionItem> _items;
        public StubLoader(IEnumerable<OptionItem> items) => _items = items.ToList();
        public string Name => RestCollectionOptionsLoader.LoaderName;
        public Task<OptionListResult> LoadAsync(OptionLoadContext context, CancellationToken ct) =>
            Task.FromResult(new OptionListResult(_items));
    }

    private sealed class StubRegistry : IOptionsLoaderRegistry
    {
        private readonly IOptionsLoader _loader;
        public StubRegistry(IOptionsLoader loader) => _loader = loader;
        public IOptionsLoader? Get(string name) => name == _loader.Name ? _loader : null;
    }

    private static ResourcePickerNodeTask Build(params OptionItem[] live)
    {
        var resolver = new ResourceResolver(new StubRegistry(new StubLoader(live)));
        return new ResourcePickerNodeTask(resolver);
    }

    private static NodeExecutionContext Context(object? selection)
    {
        var inputs = new Dictionary<string, object>
        {
            ["serverConfigId"] = "srv1",
            ["path"] = "pets",
            ["labelField"] = "name",
            ["valueField"] = "id",
        };
        if (selection != null) inputs["selection"] = selection;
        return new NodeExecutionContext(
            WorkflowDefinitionId.New(), Guid.NewGuid(), NodeId.Create("picker-1"),
            inputs, new Dictionary<string, object>());
    }

    private static object Selection(string value) =>
        new Dictionary<string, object> { ["value"] = value, ["label"] = "cached", ["mode"] = "list" };

    [Fact]
    public async Task Picks_ResolvedValueAndFreshLabel()
    {
        var task = Build(new OptionItem("Rex", "pet_rex"), new OptionItem("Fluffy", "pet_fluffy"));
        var result = await task.ExecuteAsync(Context(Selection("pet_rex")), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("pet_rex", success.Outputs!["value"]);
        Assert.Equal("Rex", success.Outputs!["label"]); // fresh label from live list, not the cached one
        // Combined record output holds both.
        var record = Assert.IsType<Dictionary<string, object>>(success.Outputs!["record"]);
        Assert.Equal("pet_rex", record["value"]);
        Assert.Equal("Rex", record["label"]);
    }

    [Fact]
    public async Task DeletedSelection_FailsClosed()
    {
        var task = Build(new OptionItem("Fluffy", "pet_fluffy"));
        var result = await task.ExecuteAsync(Context(Selection("pet_rex")), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("pet_rex", failure.ErrorMessage);
    }

    [Fact]
    public async Task NoSelection_Fails()
    {
        var task = Build(new OptionItem("Rex", "pet_rex"));
        var result = await task.ExecuteAsync(Context(selection: null), CancellationToken.None);
        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    [Fact]
    public async Task NoConnection_Fails()
    {
        var task = Build(new OptionItem("Rex", "pet_rex"));
        var ctx = new NodeExecutionContext(
            WorkflowDefinitionId.New(), Guid.NewGuid(), NodeId.Create("picker-1"),
            new Dictionary<string, object> { ["path"] = "pets", ["selection"] = Selection("pet_rex") },
            new Dictionary<string, object>());
        var result = await task.ExecuteAsync(ctx, CancellationToken.None);
        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("server configuration", failure.ErrorMessage);
    }
}
