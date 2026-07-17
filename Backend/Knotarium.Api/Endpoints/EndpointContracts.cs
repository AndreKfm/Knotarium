// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Core.Domain;
using Knotarium.Features.OpenApi;

namespace Knotarium.Api;

// Request/response DTOs for the minimal-API endpoints. These were previously declared in the global
// namespace at the tail of Program.cs; they live here so the composition root stays thin. Access
// modifiers are unchanged (public where consumed by tests / other assemblies, internal otherwise).

internal record ImportSpecRequest(string? Content = null, string? SpecId = null, string? Url = null, bool AllowInsecureCertificate = false);
internal record CreateServerConfigRequest(string? Name, string? BaseUrl, Dictionary<string, string>? ServerVariables, string? SecuritySchemeType, string? CredentialRef, bool AllowInsecureCertificate = false);
internal record UpdateServerConfigRequest(string? Name, string? BaseUrl, Dictionary<string, string>? ServerVariables, string? SecuritySchemeType, string? CredentialRef, bool AllowInsecureCertificate = false);

internal record ImportSpecResponse(
    string Id, int VersionNumber, string Title, string OriginalFormat,
    IReadOnlyList<OperationGroup> Groups, IReadOnlyList<Knotarium.Core.Domain.OpenApi.ApiSchema> Schemas,
    IReadOnlyList<string>? DefaultServers = null);

internal record SpecSummaryResponse(
    string Id, string Title, string ApiVersion,
    int LatestVersionNumber, DateTimeOffset ImportedAtUtc, string OriginalFormat);

public record SaveVersionRequest(IReadOnlyList<NodeDefinition> Nodes, IReadOnlyList<EdgeDefinition> Edges);

/// <summary>Body for the Condition editor's last-run value lookup: the operand refs to resolve.</summary>
public record ConditionValuesRequest(IReadOnlyList<string> Refs);
public record SetArmingRequest(bool Armed);
public record SimulateSignalRequest(string? Kind, string Type, string? TargetId, Dictionary<string, string>? Payload);
public record BulkDeleteExecutionsRequest(List<Guid>? Ids, bool? All, string? Status);

public record SetErrorWorkflowRequest(string? WorkflowId);
public record SetEnabledRequest(bool Enabled);
public record CreateBackupRequest(string? Passphrase, bool? IncludeRunHistory, bool? UseServerKey);
public record StartExecutionRequest(string WorkflowDefinitionId, Dictionary<string, object>? InputVariables);
public record ResumeExecutionRequest(string? Token, JsonElement Payload);
public record ManualDecisionRequest(string Decision, string? Reason, string? ExpectedAttemptId);
public record ReplayExecutionRequest(string FromNodeId, Guid? TargetVersionId, bool? MockSideEffects);
public record CreateCredentialRequest(string Id, string Name, string Value);
public record CreateNotificationChannelRequest(string Id, string Name, string Type, JsonElement? Config, bool IsDefaultFailureAlert);
public record WorkflowScheduleSummary(string NodeId, string CronExpression, string TimeZoneId, DateTimeOffset NextFireAtUtc, bool IsActive);

/// <summary>
/// Lightweight version metadata for the history panel — deliberately omits the node/edge payloads
/// so the list endpoint stays cheap. Full payloads are served only by the version-detail endpoint.
/// </summary>
public record WorkflowVersionSummary(
    Guid Id,
    int VersionNumber,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    string? Label,
    string Origin,
    bool IsActive,
    Guid? RestoredFromVersionId,
    int NodeCount,
    int ExecutionCount);

public record WorkflowVersionListResponse(
    IReadOnlyList<WorkflowVersionSummary> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>A single activation event from the append-only activation log.</summary>
public record WorkflowActivationEvent(
    Guid Id,
    Guid WorkflowVersionId,
    DateTimeOffset ActivatedAtUtc,
    string? ActivatedBy,
    string? ActivationReason,
    Guid? RestoredFromVersionId,
    Guid? PreviousActiveVersionId,
    string? CorrelationId);

public record WorkflowActivationHistoryResponse(
    IReadOnlyList<WorkflowActivationEvent> Items,
    int Page,
    int PageSize,
    int TotalCount);
