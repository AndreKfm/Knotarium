// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Features.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddBuiltInNodes() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the built-in node slice: the DI-backed node-task registry, every built-in node task
/// (transient — one per execution), and the shared Roslyn script compiler used by inline-code and
/// custom-package C# (a singleton that owns a process-wide compile cache).
/// </summary>
public static class NodesServiceCollectionExtensions
{
    public static IServiceCollection AddBuiltInNodes(this IServiceCollection services)
    {
        services.AddScoped<INodeTaskRegistry, DependencyInjectionNodeTaskRegistry>();

        // File-access enforcement for the file nodes. The guard is always registered; the policy provider
        // defaults to deny-all (fail closed) and is overridden by the settings-backed provider (AddSettings).
        services.AddScoped<IFileAccessPolicy, FileAccessGuard>();
        services.TryAddSingleton<IFileAccessPolicyProvider, DeniedFileAccessPolicyProvider>();

        // Capability switch for privileged nodes (inline code, database). Defaults to all-off (fail closed);
        // overridden by the settings-backed store in AddSettings.
        services.TryAddSingleton<ICapabilityPolicy, DeniedCapabilityPolicy>();

        // Chat completion for the AI prompt node. Defaults to a not-configured stub (fail closed);
        // overridden by the real service in AddAiGeneration.
        services.TryAddSingleton<Knotarium.Core.Contracts.Ai.IChatCompletionService, UnconfiguredChatCompletionService>();

        // Tool-enabled chat + tool runner for the AI agent node. Both default to fail-closed stubs,
        // overridden by the real service (AddAiGeneration) and the real runner (AddExecution) in a full host.
        services.TryAddSingleton<Knotarium.Core.Contracts.Ai.IAgentChatService, UnconfiguredAgentChatService>();
        services.TryAddSingleton<Knotarium.Core.Contracts.Ai.IAgentToolRunner, UnavailableAgentToolRunner>();

        services.AddTransient<StartNodeTask>();
        services.AddTransient<ConditionNodeTask>();
        services.AddTransient<SetVariableNodeTask>();
        services.AddTransient<SetVariablesNodeTask>();
        services.AddTransient<HttpRequestNodeTask>();
        services.AddTransient<DelayNodeTask>();
        services.AddTransient<LogNodeTask>();
        services.AddTransient<ForLoopNodeTask>();
        services.AddTransient<JoinNodeTask>();
        services.AddTransient<EndNodeTask>();
        services.AddTransient<SendNotificationNodeTask>();
        services.AddTransient<InlineCodeNodeTask>();
        services.AddTransient<ResourcePickerNodeTask>();
        services.AddTransient<DbQueryNodeTask>();
        services.AddTransient<FileReadNodeTask>();
        services.AddTransient<FileWriteNodeTask>();
        services.AddTransient<SmtpSendNodeTask>();
        services.AddTransient<ImapFetchNodeTask>();
        services.AddTransient<MqPublishNodeTask>();
        services.AddTransient<AiPromptNodeTask>();
        services.AddTransient<AiRouterNodeTask>();
        services.AddTransient<AiVerifyNodeTask>();
        services.AddTransient<AiDiffNodeTask>();
        services.AddTransient<AiAgentNodeTask>();

        // Shared Roslyn compiler for inline-code + custom-package C# (owns a process-wide compile cache).
        services.AddSingleton<CSharpScriptCompiler>();
        return services;
    }
}
