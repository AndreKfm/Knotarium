// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Knotarium.Core.Domain;

/// <summary>
/// Contains organizational metadata for a workflow, such as its assigned group
/// and its failure-alert routing.
/// </summary>
public record WorkflowMetadata(string? Group = null, FailureAlertConfig? FailureAlert = null);

/// <summary>
/// Per-workflow failure-alert routing. When absent (or <see cref="Mode"/> is
/// <see cref="FailureAlertModes.Inherit"/>), the workflow inherits the global default channels
/// (those marked <see cref="NotificationChannel.IsDefaultFailureAlert"/>).
/// </summary>
public record FailureAlertConfig(string Mode, IReadOnlyList<string>? ChannelIds = null);

/// <summary>Valid values for <see cref="FailureAlertConfig.Mode"/>.</summary>
public static class FailureAlertModes
{
    /// <summary>Use the globally configured default channels.</summary>
    public const string Inherit = "Inherit";

    /// <summary>Suppress failure alerts for this workflow entirely.</summary>
    public const string Off = "Off";

    /// <summary>Alert only the channels listed in <see cref="FailureAlertConfig.ChannelIds"/>.</summary>
    public const string Custom = "Custom";
}
