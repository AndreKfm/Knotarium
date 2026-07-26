// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knotarium.Tests.Nodes;

/// <summary>
/// Drift guard between the built-in node catalog and the executors that back it.
///
/// <para>Being in the catalog is what puts a node in the palette, lets it pass graph validation and
/// lets a workflow containing it publish. None of those steps consults the task registry, so a node
/// can be fully "available" and still have nowhere to run — <c>switch</c>, <c>transform</c> and
/// <c>merge</c> all shipped that way, failing every run with "No task implementation registered for
/// type 'X'" only once someone actually pressed Run.</para>
///
/// <para>The exemptions below are the node types that legitimately never resolve to an
/// <see cref="INodeTask"/>, each with the mechanism that handles it instead. Adding to this list should
/// take an argument; adding a catalog entry without an executor should not be possible silently.</para>
/// </summary>
public class BuiltInCatalogExecutorCoverageTests
{
    /// <summary>Catalog ids that intentionally have no INodeTask, and what runs them instead.</summary>
    private static readonly Dictionary<string, string> ExecutedElsewhere = new(StringComparer.OrdinalIgnoreCase)
    {
        ["parallelForEach"] = "Run by ParallelForEachNodeRunner, special-cased in WorkflowExecutor before the registry is consulted.",
        ["subflow"]         = "Never executes: WorkflowCompiler.InlineSubflowsAsync flattens it into the parent plan at compile time.",
        ["externalDevice"]  = "Dispatched by the reactive layer via its dynamic evt:/act: pins, not the control-flow registry.",
        ["stickyNote"]      = "Canvas annotation. Inert — no ports, never scheduled.",
        ["group"]           = "Canvas annotation. Inert — no ports, never scheduled.",
    };

    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBuiltInNodes();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Every_catalog_node_either_resolves_to_an_executor_or_is_a_documented_exemption()
    {
        using var provider = BuildContainer();
        var registry = provider.GetRequiredService<INodeTaskRegistry>();
        var catalog = new InMemoryNodePackageManifestProvider().GetAllManifests();

        Assert.NotEmpty(catalog);

        var unrunnable = catalog
            .Where(m => !m.TriggerOnly)                                  // trigger entry points are completed by the engine
            .Where(m => !ExecutedElsewhere.ContainsKey(m.Id.Value))
            .Where(m => !HasExecutor(registry, m.Id.Value))
            .Select(m => $"{m.Id.Value} ({m.DisplayName})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(unrunnable.Count == 0,
            "These node types are offered in the palette and pass validation, but have no executor, so "
            + "every run reaching one fails at runtime. Implement an INodeTask and register it in "
            + "NodesServiceCollectionExtensions + DependencyInjectionNodeTaskRegistry, or add a documented "
            + "exemption to ExecutedElsewhere:\n  " + string.Join("\n  ", unrunnable));
    }

    /// <summary>
    /// Whether an executor is wired up for this node type — which is a different question from whether
    /// it can be constructed in a bare test container.
    ///
    /// <para>The defect being guarded against is <c>GetTask</c> returning <b>null</b>: no mapping, no
    /// binary package, no database package, so the run dies with "No task implementation registered".
    /// A DI resolution <b>throw</b> means the opposite — the type IS mapped and registered, and the only
    /// thing missing is a platform service (IHttpClientFactory, ISecretResolver, …) that the real host
    /// supplies and this container deliberately does not. Chasing those here would turn the guard into a
    /// second copy of the application's composition root that rots on its own schedule.</para>
    ///
    /// <para>The null case still catches a mapping whose type was never registered with the container:
    /// <c>GetService</c> returns null for an unregistered type rather than throwing, and GetTask then
    /// falls through to the binary and database lookups and returns null.</para>
    /// </summary>
    private static bool HasExecutor(INodeTaskRegistry registry, string nodeType)
    {
        try
        {
            return registry.GetTask(nodeType) is not null;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [Fact]
    public void Exemptions_stay_honest_about_what_is_in_the_catalog()
    {
        // A stale exemption is its own hazard: it would mask a node that later gained a catalog entry
        // but no executor. Every exemption must still name a real catalog node.
        var catalogIds = new InMemoryNodePackageManifestProvider()
            .GetAllManifests()
            .Select(m => m.Id.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = ExecutedElsewhere.Keys.Where(id => !catalogIds.Contains(id)).OrderBy(x => x).ToList();

        Assert.True(stale.Count == 0,
            "Exemptions naming node types that are no longer in the catalog — remove them:\n  " + string.Join("\n  ", stale));
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("transform")]
    [InlineData("merge")]
    public void The_three_nodes_that_shipped_without_an_executor_now_resolve(string nodeType)
    {
        using var provider = BuildContainer();
        var registry = provider.GetRequiredService<INodeTaskRegistry>();

        Assert.NotNull(registry.GetTask(nodeType));
    }
}
