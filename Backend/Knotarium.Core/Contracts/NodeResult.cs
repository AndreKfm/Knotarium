// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

public sealed record NodeResult(
    string OutputName,
    JsonElement? Payload,
    NodeExecutionStatus Status);
