// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Opens a client-side tracing span for an outbound HTTP call made from within a node. A narrow view
/// over the execution telemetry aggregate so node implementations can emit spans without depending on
/// the Execution slice that owns the meters/activity source. Optional at the call site — a null
/// telemetry means tracing is not wired.
/// </summary>
public interface IOutboundHttpTelemetry
{
    /// <summary>Starts a <c>workflow.capability.http</c> client activity for the request, or returns null if no listener is attached.</summary>
    Activity? StartOutboundHttpActivity(Uri uri, string method, NodeExecutionContext context);
}
