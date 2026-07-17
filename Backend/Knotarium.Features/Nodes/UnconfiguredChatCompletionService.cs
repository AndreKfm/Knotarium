// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Fallback <see cref="IChatCompletionService"/>: throws a clear not-configured error at call time.
/// Registered as the default so a host that wires the built-in nodes without the AI slice still
/// resolves <see cref="AiPromptNodeTask"/> — the node then fails with this message instead of the
/// registry blowing up on a missing dependency. The AI slice's real service overrides this.
/// </summary>
public sealed class UnconfiguredChatCompletionService : IChatCompletionService
{
    public Task<string> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "The AI provider is not configured. Choose a provider, model, and API-key credential in Settings → AI Provider.");
}
