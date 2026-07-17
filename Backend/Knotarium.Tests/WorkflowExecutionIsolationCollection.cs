// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Knotarium.Tests;

/// <summary>
/// Serializes workflow execution tests that spin up background workers or an in-process API host.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkflowExecutionIsolationCollection
{
    /// <summary>
    /// The xUnit collection name used by workflow execution integration tests.
    /// </summary>
    public const string Name = "WorkflowExecutionIsolation";
}