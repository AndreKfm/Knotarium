// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Re-binds a workflow's live trigger registrations to the supplied definition's nodes. Each concrete
/// implementation owns one trigger kind (schedules, polling triggers, …) and is a host-owned bridge over
/// the runtime's registration state.
/// </summary>
/// <remarks>
/// Publish and activation depend on the <em>set</em> of registered synchronizers (injected as
/// <c>IEnumerable&lt;IWorkflowTriggerSynchronizer&gt;</c>) rather than any concrete type, so the
/// publishing services can live in <c>Knotarium.Features</c> without referencing the host bridges.
/// </remarks>
public interface IWorkflowTriggerSynchronizer
{
    /// <summary>Reconciles this synchronizer's persisted trigger rows with the workflow's current nodes.</summary>
    Task SyncAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);
}
