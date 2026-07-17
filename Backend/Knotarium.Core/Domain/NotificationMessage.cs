// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Knotarium.Core.Domain;

/// <summary>
/// The transport-agnostic content every notification sender needs: a short <see cref="Title"/> and a
/// longer <see cref="Body"/>. <see cref="Data"/> carries optional structured fields that
/// machine-readable transports (the generic webhook) merge into their payload; human transports
/// (Slack, e-mail) ignore it.
/// </summary>
public record NotificationMessage(
    string Title,
    string Body,
    IReadOnlyDictionary<string, object?>? Data = null);
