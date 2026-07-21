// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { Edge as FlowEdge, Node as FlowNode } from '@xyflow/react';
import type { WorkflowDefinition, ExecutionInstance, ExecutionJournal, NodePackageSummary, WorkflowScheduleSummary, WorkflowVersion, WorkflowVersionSummary, WorkflowVersionListResponse, WorkflowVersionOrigin, ActiveWorkflowVersion, RestoreVersionResult, NodeDefinition, EdgeDefinition, NodeId, WorkflowGroupContainer, ReplayResult, ReplayWarning, ReplayLineageEntry, CompilationDiagnostic, FailureAlertConfig, FailureAlertMode, NotificationChannel, NotificationChannelType, InlineCodeTestResult, LoadOptionsResult, BundleInstallResponse, CredentialSummary, BundleManifestInput, BackupManifest, RestoreReport, TemplateExportRequest, TemplatePortabilizationReport, TemplateInspectResponse, TemplateInstallResponse, GalleryTemplate, TemplatePayloadResponse, TemplateManifest, TemplateCredentialSlot, TemplateCompatibility, ParameterValues, ConditionLastRunResponse, VersionInfo, ProviderDescriptor, ExternalSystemInfo, ExternalTargetInfo, ExternalTargetEdit, TargetStatus, ImportProviderDescriptor, ImportGranularity, ImportPreviewResponse, ImportInstallResponse, ImportTargetStrategy, AiGenerationJobResult, AiProviderConfigResponse, SetAiProviderConfigInput, AiProviderTestResponse, AiProviderModelsResponse, AuthStatus, AuthUser, FileAccessPolicyDto, CapabilityPolicyDto, RetentionConfigDto, DiskSpaceConfigDto, SandboxSettingsDto } from '../types';

const API_BASE = '/api';

type JsonObject = Record<string, unknown>;

type WorkflowPublishResponse = {
  version: WorkflowVersion;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// Explicit size of a resizable container (group / loop box). A NodeResizer drag (xyflow v12)
// writes the new size to the node's top-level width/height and leaves node.style untouched, so
// read that first and fall back to the creation/reload style size. Non-container nodes have
// neither and stay undefined.
function containerWidth(n: FlowNode): number | undefined {
  if (typeof n.width === 'number') return n.width;
  return n.style?.width ? Number(n.style.width) : undefined;
}

function containerHeight(n: FlowNode): number | undefined {
  if (typeof n.height === 'number') return n.height;
  return n.style?.height ? Number(n.style.height) : undefined;
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function getString(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function mapNodeId(value: unknown): NodeId | undefined {
  if (typeof value === 'string') {
    return { value };
  }

  if (isRecord(value) && typeof value.value === 'string') {
    return { value: value.value };
  }

  return undefined;
}

function mapNodeDefinition(node: unknown): NodeDefinition {
  const nodeRecord = isRecord(node) ? node : {};

  return {
    ...(nodeRecord as Partial<NodeDefinition>),
    id: mapNodeId(nodeRecord.id) ?? { value: '' },
    type: getString(nodeRecord.type),
    properties: isRecord(nodeRecord.properties) ? (nodeRecord.properties as NodeDefinition['properties']) : {},
  };
}

function mapTemplatePayload(raw: Record<string, unknown>): TemplatePayloadResponse {
  return {
    manifest: raw.manifest as TemplateManifest,
    credentialSlots: asArray(raw.credentialSlots) as TemplateCredentialSlot[],
    compatibility: (raw.compatibility as TemplateCompatibility) ?? { supported: true, warnings: [] },
    nodes: asArray(raw.nodes).map(mapNodeDefinition),
    edges: asArray(raw.edges).map(mapEdgeDefinition),
  };
}

function mapEdgeDefinition(edge: unknown): EdgeDefinition {
  const edgeRecord = isRecord(edge) ? edge : {};

  return {
    ...(edgeRecord as Partial<EdgeDefinition>),
    id: getString(edgeRecord.id),
    from: mapNodeId(edgeRecord.from) ?? { value: '' },
    output: getString(edgeRecord.output),
    to: mapNodeId(edgeRecord.to) ?? { value: '' },
    input: getString(edgeRecord.input),
  };
}

export class ApiError extends Error {
  status: number;
  data: unknown;

  constructor(message: string, status: number, data: unknown) {
    super(message);
    this.status = status;
    this.data = data;
    Object.setPrototypeOf(this, ApiError.prototype);
  }
}

async function handleResponse<T>(response: Response): Promise<T> {
  const text = await response.text();
  let data: unknown;
  try {
    data = text ? (JSON.parse(text) as JsonObject | unknown[]) : null;
  } catch {
    data = text;
  }

  if (!response.ok) {
    // Signal a lost/expired session so the auth gate can send the user back to login. Harmless during
    // a failed login attempt (the gate is already on the login screen). Excludes 401s from the login/
    // setup calls, which surface their own "invalid credentials" message inline.
    if (response.status === 401 && typeof window !== 'undefined' && !/\/api\/auth\/(login|setup)$/.test(response.url)) {
      window.dispatchEvent(new CustomEvent('kg-unauthorized'));
    }
    const message = isRecord(data)
      ? (typeof data.message === 'string' ? data.message : typeof data.title === 'string' ? data.title : undefined)
      : (typeof data === 'string' ? data : undefined);
    throw new ApiError(message ?? 'API request failed', response.status, data);
  }
  return data as T;
}

function mapFailureAlert(value: unknown): FailureAlertConfig | null {
  if (!isRecord(value)) {
    return null;
  }

  const mode = value.mode;
  const normalizedMode: FailureAlertMode =
    mode === 'Off' || mode === 'Custom' ? mode : 'Inherit';
  const channelIds = asArray(value.channelIds).filter((id): id is string => typeof id === 'string');
  return { mode: normalizedMode, channelIds };
}

// Map database flat-string IDs to frontend structured { value: string } format
function mapWorkflowDefinition(data: unknown): WorkflowDefinition {
  const workflowRecord = isRecord(data) ? data : {};

  return {
    ...(workflowRecord as Partial<WorkflowDefinition>),
    id: mapNodeId(workflowRecord.id) ?? { value: '' },
    name: getString(workflowRecord.name),
    nodes: asArray(workflowRecord.nodes).map(mapNodeDefinition),
    edges: asArray(workflowRecord.edges).map(mapEdgeDefinition),
    metadata: isRecord(workflowRecord.metadata)
      ? {
          group: typeof workflowRecord.metadata.group === 'string' ? workflowRecord.metadata.group : null,
          failureAlert: mapFailureAlert(workflowRecord.metadata.failureAlert),
        }
      : null,
    // Absent means the field predates the feature; treat as enabled.
    isEnabled: workflowRecord.isEnabled === undefined ? true : Boolean(workflowRecord.isEnabled),
  };
}

function mapWorkflowVersion(data: unknown): WorkflowVersion {
  const versionRecord = isRecord(data) ? data : {};

  return {
    ...(versionRecord as Partial<WorkflowVersion>),
    id: getString(versionRecord.id),
    workflowDefinitionId: mapNodeId(versionRecord.workflowDefinitionId) ?? { value: '' },
    versionNumber: typeof versionRecord.versionNumber === 'number' ? versionRecord.versionNumber : 0,
    nodes: asArray(versionRecord.nodes).map(mapNodeDefinition),
    edges: asArray(versionRecord.edges).map(mapEdgeDefinition),
    createdAt: getString(versionRecord.createdAt),
  };
}

function getStringOrNull(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function mapVersionOrigin(value: unknown): WorkflowVersionOrigin {
  return value === 'Restored' || value === 'Imported' ? value : 'Published';
}

// Maps a single item of the paginated version-list envelope. Metadata only —
// nodes/edges are intentionally absent from this endpoint.
function mapWorkflowVersionSummary(data: unknown): WorkflowVersionSummary {
  const record = isRecord(data) ? data : {};

  return {
    id: getString(record.id),
    versionNumber: typeof record.versionNumber === 'number' ? record.versionNumber : 0,
    createdAt: getString(record.createdAt),
    createdBy: getStringOrNull(record.createdBy),
    label: getStringOrNull(record.label),
    origin: mapVersionOrigin(record.origin),
    isActive: Boolean(record.isActive),
    restoredFromVersionId: getStringOrNull(record.restoredFromVersionId),
    nodeCount: typeof record.nodeCount === 'number' ? record.nodeCount : 0,
    executionCount: typeof record.executionCount === 'number' ? record.executionCount : 0,
  };
}

function mapWorkflowVersionListResponse(data: unknown): WorkflowVersionListResponse {
  const record = isRecord(data) ? data : {};

  return {
    items: asArray(record.items).map(mapWorkflowVersionSummary),
    page: typeof record.page === 'number' ? record.page : 1,
    pageSize: typeof record.pageSize === 'number' ? record.pageSize : 0,
    totalCount: typeof record.totalCount === 'number' ? record.totalCount : 0,
  };
}

function mapActiveWorkflowVersion(data: unknown): ActiveWorkflowVersion {
  const versionRecord = isRecord(data) ? data : {};

  return {
    ...(versionRecord as Partial<ActiveWorkflowVersion>),
    workflowDefinitionId: mapNodeId(versionRecord.workflowDefinitionId) ?? { value: '' },
    workflowVersionId: getString(versionRecord.workflowVersionId),
    activatedAtUtc: getString(versionRecord.activatedAtUtc),
  };
}

// Map database flat-string execution details to frontend format
function mapExecutionInstance(data: unknown): ExecutionInstance {
  const executionRecord = isRecord(data) ? data : {};

  return {
    ...(executionRecord as Partial<ExecutionInstance>),
    id: getString(executionRecord.id),
    workflowDefinitionId: mapNodeId(executionRecord.workflowDefinitionId) ?? { value: '' },
    workflowVersionId: getString(executionRecord.workflowVersionId) || undefined,
    status: (executionRecord.status as ExecutionInstance['status']) ?? 'Pending',
    createdAt: getString(executionRecord.createdAt),
    updatedAt: getString(executionRecord.updatedAt),
    globalVariables: isRecord(executionRecord.globalVariables) ? (executionRecord.globalVariables as ExecutionInstance['globalVariables']) : {},
    errorOfExecutionId: mapNodeId(executionRecord.errorOfExecutionId)?.value || undefined,
    nodeStates: asArray(executionRecord.nodeStates).map((state) => {
      const stateRecord = isRecord(state) ? state : {};
      return {
        ...stateRecord,
        nodeId: mapNodeId(stateRecord.nodeId) ?? { value: '' },
      };
    }) as ExecutionInstance['nodeStates'],
  };
}

// Map database flat-string journal details to frontend format
function mapExecutionJournal(data: unknown): ExecutionJournal {
  const journalRecord = isRecord(data) ? data : {};
  const eventType = journalRecord.eventType || journalRecord.EventType;
  const timestamp = journalRecord.timestamp || journalRecord.Timestamp;
  const message = journalRecord.message || journalRecord.Message;
  const nodeId = journalRecord.nodeId || journalRecord.NodeId;
  const dataPayload = journalRecord.data || journalRecord.Data;

  return {
    ...(journalRecord as Omit<ExecutionJournal, 'nodeId' | 'eventType' | 'timestamp' | 'message' | 'data'>),
    id: getString(journalRecord.id),
    executionInstanceId: getString(journalRecord.executionInstanceId),
    eventType: getString(eventType),
    timestamp: getString(timestamp),
    message: getString(message),
    data: (isRecord(dataPayload) ? dataPayload : {}) as ExecutionJournal['data'],
    nodeId: mapNodeId(nodeId),
  };
}

export const api = {
  // ── Authentication ─────────────────────────────────────────────────────────
  async getAuthStatus(): Promise<AuthStatus> {
    const response = await fetch(`${API_BASE}/auth/status`);
    return handleResponse<AuthStatus>(response);
  },

  async setupFirstAdmin(username: string, password: string): Promise<{ username: string }> {
    const response = await fetch(`${API_BASE}/auth/setup`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    });
    return handleResponse<{ username: string }>(response);
  },

  async login(username: string, password: string): Promise<{ username: string }> {
    const response = await fetch(`${API_BASE}/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    });
    return handleResponse<{ username: string }>(response);
  },

  async logout(): Promise<void> {
    await fetch(`${API_BASE}/auth/logout`, { method: 'POST' });
  },

  async listUsers(): Promise<AuthUser[]> {
    const response = await fetch(`${API_BASE}/auth/users`);
    return handleResponse<AuthUser[]>(response);
  },

  async createUser(username: string, password: string, role?: string): Promise<AuthUser> {
    const response = await fetch(`${API_BASE}/auth/users`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password, role }),
    });
    return handleResponse<AuthUser>(response);
  },

  async deleteUser(id: string): Promise<void> {
    const response = await fetch(`${API_BASE}/auth/users/${id}`, { method: 'DELETE' });
    await handleResponse<unknown>(response);
  },

  async changeOwnPassword(newPassword: string): Promise<void> {
    const response = await fetch(`${API_BASE}/auth/change-password`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ newPassword }),
    });
    await handleResponse<unknown>(response);
  },

  async getWorkflows(): Promise<WorkflowDefinition[]> {
    const response = await fetch(`${API_BASE}/workflows`);
    const data = await handleResponse<unknown[]>(response);
    return data.map(mapWorkflowDefinition);
  },

  async getWorkflow(id: string): Promise<WorkflowDefinition> {
    const response = await fetch(`${API_BASE}/workflows/${id}`);
    const data = await handleResponse<unknown>(response);
    return mapWorkflowDefinition(data);
  },

  /** Clone an entire workflow into a new "(copy)" draft (disabled, no published version). Returns it. */
  async duplicateWorkflow(id: string): Promise<WorkflowDefinition> {
    const response = await fetch(`${API_BASE}/workflows/${id}/duplicate`, { method: 'POST' });
    const data = await handleResponse<unknown>(response);
    return mapWorkflowDefinition(data);
  },

  /**
   * Start an AI workflow-generation job. Returns the job id to poll with {@link getGenerationJob}.
   * When {@link currentWorkflow} is supplied, the job REFINES that existing workflow per the intent
   * instead of generating a new one from scratch.
   */
  async generateWorkflow(intent: string, currentWorkflow?: WorkflowDefinition | null): Promise<{ jobId: string }> {
    const response = await fetch(`${API_BASE}/ai/generate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(currentWorkflow ? { intent, workflow: currentWorkflow } : { intent }),
    });
    const data = await handleResponse<{ jobId: string }>(response);
    return { jobId: getString((data as JsonObject).jobId) };
  },

  /** Generate/modify Inline Code node body from a prompt. Synchronous (one completion). Throws on a
   *  provider/config error with the backend's message so the editor can show it inline. */
  async generateInlineCode(prompt: string, currentCode?: string, language?: string): Promise<string> {
    const response = await fetch(`${API_BASE}/ai/inline-code`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ prompt, currentCode: currentCode ?? null, language: language ?? null }),
    });
    const data = await handleResponse<{ code: string }>(response);
    return getString((data as JsonObject).code);
  },

  /** Poll a generation job. The workflow is mapped identically to getWorkflow so the canvas can load it. */
  async getGenerationJob(jobId: string): Promise<AiGenerationJobResult> {
    const response = await fetch(`${API_BASE}/ai/generate/${jobId}`);
    const data = await handleResponse<unknown>(response);
    const record = isRecord(data) ? data : {};
    return {
      jobId: getString(record.jobId, jobId),
      status: getString(record.status, 'Running'),
      workflow: record.workflow ? mapWorkflowDefinition(record.workflow) : null,
      openSlots: asArray(record.openSlots).map((s) => getString(s)),
      diagnostics: asArray(record.diagnostics).map((s) => getString(s)),
      attempts: typeof record.attempts === 'number' ? record.attempts : 0,
      error: typeof record.error === 'string' ? record.error : null,
    };
  },

  /** Read the active AI provider config (vendor/model/credential ref — never the key). */
  async getAiProviderConfig(): Promise<AiProviderConfigResponse> {
    const response = await fetch(`${API_BASE}/settings/ai-provider`);
    return handleResponse<AiProviderConfigResponse>(response);
  },

  /** Set the active AI provider config. */
  async setAiProviderConfig(input: SetAiProviderConfigInput): Promise<AiProviderConfigResponse> {
    const response = await fetch(`${API_BASE}/settings/ai-provider`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
    return handleResponse<AiProviderConfigResponse>(response);
  },

  /** Test the supplied provider config end-to-end (a tiny real completion). Never throws on a provider error. */
  async testAiProvider(input: SetAiProviderConfigInput): Promise<AiProviderTestResponse> {
    const response = await fetch(`${API_BASE}/settings/ai-provider/test`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
    return handleResponse<AiProviderTestResponse>(response);
  },

  /** Best-effort live model ids for the supplied provider config (empty = fall back to the curated list). */
  async getAiProviderModels(input: SetAiProviderConfigInput): Promise<AiProviderModelsResponse> {
    const response = await fetch(`${API_BASE}/settings/ai-provider/models`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
    return handleResponse<AiProviderModelsResponse>(response);
  },

  // The list endpoint returns a paginated metadata envelope (no nodes/edges).
  // We unwrap it to an array of summaries so existing callers that only read
  // id / versionNumber keep working. For the full payload of a single version,
  // use getWorkflowVersionDetail.
  async getWorkflowVersions(id: string): Promise<WorkflowVersionSummary[]> {
    const response = await fetch(`${API_BASE}/workflows/${id}/versions`);
    const data = await handleResponse<unknown>(response);
    return mapWorkflowVersionListResponse(data).items;
  },

  // Paginated fetch for the version-history panel.
  async getWorkflowVersionsPage(id: string, page: number, pageSize: number): Promise<WorkflowVersionListResponse> {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    const response = await fetch(`${API_BASE}/workflows/${id}/versions?${params.toString()}`);
    const data = await handleResponse<unknown>(response);
    return mapWorkflowVersionListResponse(data);
  },

  // Full single version (nodes + edges + metadata) from the detail endpoint.
  async getWorkflowVersionDetail(id: string, versionId: string): Promise<WorkflowVersion> {
    const response = await fetch(`${API_BASE}/workflows/${id}/versions/${versionId}`);
    const data = await handleResponse<unknown>(response);
    return mapWorkflowVersion(data);
  },

  async getActiveWorkflowVersion(id: string): Promise<ActiveWorkflowVersion | null> {
    const response = await fetch(`${API_BASE}/workflows/${id}/active-version`);
    if (response.status === 204) {
      return null;
    }

    const data = await handleResponse<unknown>(response);
    return mapActiveWorkflowVersion(data);
  },

  // Fork-forward restore. Creates a new immutable version copied from {versionId}.
  // With activate=false (default) the forward copy stays inactive and `warnings`
  // carries compatibility findings; with activate=true the backend additionally
  // activates it and may 400 (compile diagnostics) or 409 (concurrency).
  async restoreVersion(id: string, versionId: string, activate: boolean): Promise<RestoreVersionResult> {
    const params = new URLSearchParams({ activate: String(activate) });
    const response = await fetch(`${API_BASE}/workflows/${id}/restore/${versionId}?${params.toString()}`, {
      method: 'POST',
    });
    const data = await handleResponse<unknown>(response);
    const record = isRecord(data) ? data : {};
    return {
      versionId: getString(record.versionId),
      versionNumber: typeof record.versionNumber === 'number' ? record.versionNumber : 0,
      origin: mapVersionOrigin(record.origin),
      restoredFromVersionId: getStringOrNull(record.restoredFromVersionId),
      activated: Boolean(record.activated),
      activatedAtUtc: getStringOrNull(record.activatedAtUtc),
      warnings: asArray(record.warnings).filter((w): w is string => typeof w === 'string'),
    };
  },

  async activateWorkflowVersion(id: string, versionId: string): Promise<ActiveWorkflowVersion> {
    const response = await fetch(`${API_BASE}/workflows/${id}/activate/${versionId}`, {
      method: 'POST',
    });
    const data = await handleResponse<unknown>(response);
    return mapActiveWorkflowVersion(data);
  },

  async getWorkflowSchedules(id: string): Promise<WorkflowScheduleSummary[]> {
    const response = await fetch(`${API_BASE}/workflows/${id}/schedules`);
    return await handleResponse<WorkflowScheduleSummary[]>(response);
  },

  async fireWorkflowSchedule(workflowId: string, nodeId: string): Promise<ExecutionInstance> {
    const response = await fetch(`${API_BASE}/workflows/${workflowId}/schedules/${nodeId}/fire`, {
      method: 'POST',
    });
    const data = await handleResponse<unknown>(response);
    return mapExecutionInstance(data);
  },

  async saveWorkflow(workflow: WorkflowDefinition): Promise<WorkflowDefinition> {
    const response = await fetch(`${API_BASE}/workflows`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(workflow),
    });
    const data = await handleResponse<unknown>(response);
    return mapWorkflowDefinition(data);
  },

  // Publish a workflow straight from a backend-shaped definition (NodeDefinition/EdgeDefinition),
  // so a freshly-created subflow is resolvable by the compiler (which reads published definitions
  // from the DB, not the draft store). The draft must already be saved.
  async publishWorkflowDefinition(workflow: WorkflowDefinition): Promise<void> {
    const response = await fetch(`${API_BASE}/workflows/${workflow.id.value}/publish`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ nodes: workflow.nodes, edges: workflow.edges }),
    });
    await handleResponse<unknown>(response);
  },

  async setWorkflowEnabled(id: string, enabled: boolean): Promise<{ enabled: boolean; cancelledExecutions: number }> {
    const response = await fetch(`${API_BASE}/workflows/${id}/enabled`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    });
    const data = await handleResponse<{ enabled?: boolean; cancelledExecutions?: number }>(response);
    return { enabled: Boolean(data?.enabled), cancelledExecutions: Number(data?.cancelledExecutions ?? 0) };
  },

  /** Delete a single execution (and its journal/node-state rows). 409 if it's still in progress. */
  async deleteExecution(id: string): Promise<void> {
    const response = await fetch(`${API_BASE}/executions/${id}`, { method: 'DELETE' });
    await handleResponse<unknown>(response);
  },

  /** Stop a run that's in progress (or stuck Running): marks it Cancelled + clears pending work items. */
  async cancelExecution(id: string): Promise<void> {
    const response = await fetch(`${API_BASE}/executions/${id}/cancel`, { method: 'POST' });
    await handleResponse<unknown>(response);
  },

  /** Bulk-delete executions: an explicit id set, or all matching the status filter. Skips in-flight runs. */
  async bulkDeleteExecutions(body: { ids?: string[]; all?: boolean; status?: string }): Promise<{ deleted: number }> {
    const response = await fetch(`${API_BASE}/executions/bulk-delete`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await handleResponse<{ deleted?: unknown }>(response);
    return { deleted: typeof (data as { deleted?: unknown })?.deleted === 'number' ? (data as { deleted: number }).deleted : 0 };
  },

  async triggerWorkflow(id: string): Promise<ExecutionInstance> {
    const response = await fetch(`${API_BASE}/workflows/${id}/trigger`, {
      method: 'POST',
    });
    const data = await handleResponse<unknown>(response);
    return mapExecutionInstance(data);
  },

  /**
   * Start a run that simulates an inbound device signal — seeded at the chosen pin's downstream node(s)
   * with a synthetic payload, exactly like a live event — instead of a generic manual run. Returns the
   * new execution id so the caller can open it.
   */
  async simulateSignal(
    id: string,
    body: { kind: 'action' | 'event'; type: string; payload?: Record<string, string> },
  ): Promise<{ id: string }> {
    const response = await fetch(`${API_BASE}/workflows/${id}/simulate-signal`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await handleResponse<{ id?: unknown }>(response);
    return { id: getString((data as { id?: unknown })?.id) };
  },

  // Global runtime arming switch (design-time vs run-time).
  async getRuntimeArming(): Promise<{ armed: boolean }> {
    const response = await fetch(`${API_BASE}/runtime/arming`);
    const data = await handleResponse<{ armed?: boolean }>(response);
    return { armed: Boolean(data?.armed) };
  },

  async setRuntimeArming(armed: boolean): Promise<{ armed: boolean }> {
    const response = await fetch(`${API_BASE}/runtime/arming`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ armed }),
    });
    const data = await handleResponse<{ armed?: boolean }>(response);
    return { armed: Boolean(data?.armed) };
  },

  async getExecution(id: string): Promise<ExecutionInstance> {
    const response = await fetch(`${API_BASE}/executions/${id}`);
    const data = await handleResponse<unknown>(response);
    return mapExecutionInstance(data);
  },

  /** Latest run (with per-node states) for a workflow, or null when it has never run.
   *  Powers the editor-side per-node input/output inspector. */
  async getLatestExecution(workflowId: string): Promise<ExecutionInstance | null> {
    const response = await fetch(`${API_BASE}/workflows/${workflowId}/latest-execution`);
    if (response.status === 204) {
      return null;
    }
    const data = await handleResponse<unknown>(response);
    return mapExecutionInstance(data);
  },

  // Resolve Condition operand refs against the workflow's most recent run (no execution). Powers the
  // editor's "Last run" value source. Returns an empty value map when the workflow has never run.
  async getConditionLastRunValues(id: string, refs: string[]): Promise<ConditionLastRunResponse> {
    const response = await fetch(`${API_BASE}/workflows/${id}/condition-values`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refs }),
    });
    return handleResponse<ConditionLastRunResponse>(response);
  },

  async getExecutions(filters?: { status?: string; search?: string }): Promise<ExecutionInstance[]> {
    const params = new URLSearchParams();
    if (filters?.status) {
      params.set('status', filters.status);
    }

    if (filters?.search) {
      params.set('search', filters.search);
    }

    const query = params.size > 0 ? `?${params.toString()}` : '';
    const response = await fetch(`${API_BASE}/executions${query}`);
    const data = await handleResponse<unknown[]>(response);
    return data.map(mapExecutionInstance);
  },

  async getExecutionJournal(id: string): Promise<ExecutionJournal[]> {
    const response = await fetch(`${API_BASE}/executions/${id}/journal`);
    const data = await handleResponse<unknown[]>(response);
    return data.map(mapExecutionJournal);
  },

  async applyManualDecision(
    executionId: string,
    nodeId: string,
    decision: 'Retry' | 'Skip' | 'Fail',
    reason?: string,
    expectedAttemptId?: string,
  ): Promise<{ message: string }> {
    const response = await fetch(`${API_BASE}/executions/${executionId}/nodes/${nodeId}/manual-decision`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ decision, reason, expectedAttemptId }),
    });

    return await handleResponse<{ message: string }>(response);
  },

  async replayExecution(
    executionId: string,
    fromNodeId: string,
    options?: { targetVersionId?: string; mockSideEffects?: boolean },
  ): Promise<ReplayResult> {
    const response = await fetch(`${API_BASE}/executions/${executionId}/replay`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        fromNodeId,
        targetVersionId: options?.targetVersionId,
        mockSideEffects: options?.mockSideEffects ?? false,
      }),
    });

    const data = await handleResponse<{ newExecutionId?: string; warnings?: ReplayWarning[] }>(response);
    return {
      newExecutionId: String(data?.newExecutionId ?? ''),
      warnings: Array.isArray(data?.warnings) ? data!.warnings! : [],
    };
  },

  async getExecutionReplays(executionId: string): Promise<ReplayLineageEntry[]> {
    const response = await fetch(`${API_BASE}/executions/${executionId}/replays`);
    const data = await handleResponse<ReplayLineageEntry[]>(response);
    return Array.isArray(data) ? data : [];
  },

  // The error-handler run started for a failed run (null when none / 204).
  async getExecutionErrorRun(executionId: string): Promise<{ id: string; status: string } | null> {
    const response = await fetch(`${API_BASE}/executions/${executionId}/error-run`);
    if (response.status === 204) {
      return null;
    }
    const data = await handleResponse<{ id?: string; status?: string }>(response);
    return data?.id ? { id: String(data.id), status: String(data.status ?? '') } : null;
  },

  async discardExecution(executionId: string): Promise<{ id: string; status: string }> {
    const response = await fetch(`${API_BASE}/executions/${executionId}/discard`, {
      method: 'POST',
    });
    return await handleResponse<{ id: string; status: string }>(response);
  },

  async getDefaultErrorWorkflow(): Promise<string | null> {
    const response = await fetch(`${API_BASE}/settings/error-workflow`);
    const data = await handleResponse<{ workflowId?: string | null }>(response);
    return data?.workflowId ?? null;
  },

  async setDefaultErrorWorkflow(workflowId: string | null): Promise<string | null> {
    const response = await fetch(`${API_BASE}/settings/error-workflow`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ workflowId }),
    });
    const data = await handleResponse<{ workflowId?: string | null }>(response);
    return data?.workflowId ?? null;
  },

  async getFileAccessPolicy(): Promise<FileAccessPolicyDto> {
    const response = await fetch(`${API_BASE}/settings/file-access`);
    return handleResponse<FileAccessPolicyDto>(response);
  },

  async setFileAccessPolicy(policy: FileAccessPolicyDto): Promise<FileAccessPolicyDto> {
    const response = await fetch(`${API_BASE}/settings/file-access`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(policy),
    });
    return handleResponse<FileAccessPolicyDto>(response);
  },

  async getCapabilityPolicy(): Promise<CapabilityPolicyDto> {
    const response = await fetch(`${API_BASE}/settings/capabilities`);
    return handleResponse<CapabilityPolicyDto>(response);
  },

  async setCapabilityPolicy(policy: CapabilityPolicyDto): Promise<CapabilityPolicyDto> {
    const response = await fetch(`${API_BASE}/settings/capabilities`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(policy),
    });
    return handleResponse<CapabilityPolicyDto>(response);
  },

  async getRetentionConfig(): Promise<RetentionConfigDto> {
    const response = await fetch(`${API_BASE}/settings/retention`);
    return handleResponse<RetentionConfigDto>(response);
  },

  async updateRetentionConfig(config: RetentionConfigDto): Promise<RetentionConfigDto> {
    const response = await fetch(`${API_BASE}/settings/retention`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(config),
    });
    return handleResponse<RetentionConfigDto>(response);
  },

  async getDiskSpaceConfig(): Promise<DiskSpaceConfigDto> {
    const response = await fetch(`${API_BASE}/settings/disk-space`);
    return handleResponse<DiskSpaceConfigDto>(response);
  },

  async updateDiskSpaceConfig(config: DiskSpaceConfigDto): Promise<DiskSpaceConfigDto> {
    const response = await fetch(`${API_BASE}/settings/disk-space`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(config),
    });
    return handleResponse<DiskSpaceConfigDto>(response);
  },

  async getSandboxSettings(): Promise<SandboxSettingsDto> {
    const response = await fetch(`${API_BASE}/settings/sandbox`);
    return handleResponse<SandboxSettingsDto>(response);
  },

  async setSandboxSettings(settings: SandboxSettingsDto): Promise<SandboxSettingsDto> {
    const response = await fetch(`${API_BASE}/settings/sandbox`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(settings),
    });
    return handleResponse<SandboxSettingsDto>(response);
  },

  getSseUrl(executionId: string): string {
    return `${API_BASE}/executions/${executionId}/events`;
  },

  mapJournalEntry(data: unknown): ExecutionJournal {
    return mapExecutionJournal(data);
  },

  async updateWorkflow(id: string, workflow: WorkflowDefinition): Promise<WorkflowDefinition> {
    const response = await fetch(`${API_BASE}/workflows/${id}`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(workflow),
    });
    const data = await handleResponse<unknown>(response);
    return mapWorkflowDefinition(data);
  },

  async deleteWorkflow(id: string): Promise<void> {
    const response = await fetch(`${API_BASE}/workflows/${id}`, {
      method: 'DELETE',
    });
    await handleResponse<void>(response);
  },

  // Archive many workflows in one call (e.g. undo a multi-workflow import). Returns how many were removed.
  async bulkDeleteWorkflows(ids: string[]): Promise<{ deleted: number; ids: string[] }> {
    const response = await fetch(`${API_BASE}/workflows/bulk-delete`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(ids),
    });
    return await handleResponse<{ deleted: number; ids: string[] }>(response);
  },

  // List archived (soft-deleted) workflows — not shown in the main dashboard list.
  async listArchivedWorkflows(): Promise<{ id: string; name: string }[]> {
    const response = await fetch(`${API_BASE}/workflows/archived`);
    return await handleResponse<{ id: string; name: string }[]>(response);
  },

  // Restore an archived workflow: re-materialize its draft from the latest version and un-archive it.
  async restoreWorkflow(id: string): Promise<{ id: string; name: string }> {
    const response = await fetch(`${API_BASE}/workflows/${encodeURIComponent(id)}/unarchive`, { method: 'POST' });
    return await handleResponse<{ id: string; name: string }>(response);
  },

  // Permanently delete an archived workflow: purge its header + version history + activation log. Irreversible.
  async permanentlyDeleteWorkflow(id: string): Promise<{ purged: boolean; id: string }> {
    const response = await fetch(`${API_BASE}/workflows/${encodeURIComponent(id)}/permanent`, { method: 'DELETE' });
    return await handleResponse<{ purged: boolean; id: string }>(response);
  },

  // Permanently delete EVERY archived workflow ("empty the trash"). Irreversible; active workflows untouched.
  async purgeAllArchivedWorkflows(): Promise<{ purged: number; ids: string[] }> {
    const response = await fetch(`${API_BASE}/workflows/archived/all`, { method: 'DELETE' });
    return await handleResponse<{ purged: number; ids: string[] }>(response);
  },

  async saveWorkflowDraft(id: string, nodes: FlowNode[], edges: FlowEdge[]): Promise<WorkflowVersion> {
    const mappedNodes = nodes.map(n => ({
      id: typeof n.id === 'string' ? { value: n.id } : n.id,
      type: n.type,
      properties: {
        ...((n.data?.properties as Record<string, unknown>) || {}),
        _metadata: {
          x: n.position.x,
          y: n.position.y,
          parentId: n.parentId || (n.data?.properties as any)?._metadata?.parentId || undefined,
          width: containerWidth(n) ?? (n.data?.properties as any)?._metadata?.width ?? undefined,
          height: containerHeight(n) ?? (n.data?.properties as any)?._metadata?.height ?? undefined,
        },
      },
    }));
    const mappedEdges = edges.map(e => ({
      id: e.id,
      from: typeof e.source === 'string' ? { value: e.source } : e.source,
      output: e.sourceHandle || 'result',
      to: typeof e.target === 'string' ? { value: e.target } : e.target,
      input: e.targetHandle || 'in',
    }));
    const response = await fetch(`${API_BASE}/workflows/${id}/versions`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ nodes: mappedNodes, edges: mappedEdges }),
    });
    const data = await handleResponse<unknown>(response);
    return mapWorkflowVersion(data);
  },

  async publishWorkflow(id: string, nodes: FlowNode[], edges: FlowEdge[]): Promise<WorkflowPublishResponse> {
    const mappedNodes = nodes.map(n => ({
      id: typeof n.id === 'string' ? { value: n.id } : n.id,
      type: n.type,
      properties: {
        ...((n.data?.properties as Record<string, unknown>) || {}),
        _metadata: {
          x: n.position.x,
          y: n.position.y,
          parentId: n.parentId || (n.data?.properties as any)?._metadata?.parentId || undefined,
          width: containerWidth(n) ?? (n.data?.properties as any)?._metadata?.width ?? undefined,
          height: containerHeight(n) ?? (n.data?.properties as any)?._metadata?.height ?? undefined,
        },
      },
    }));
    const mappedEdges = edges.map(e => ({
      id: e.id,
      from: typeof e.source === 'string' ? { value: e.source } : e.source,
      output: e.sourceHandle || 'result',
      to: typeof e.target === 'string' ? { value: e.target } : e.target,
      input: e.targetHandle || 'in',
    }));
    const response = await fetch(`${API_BASE}/workflows/${id}/publish`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ nodes: mappedNodes, edges: mappedEdges }),
    });
    const data = await handleResponse<unknown>(response);
    const publishRecord = isRecord(data) ? data : {};

    return {
      ...(publishRecord as Omit<WorkflowPublishResponse, 'version'>),
      version: mapWorkflowVersion(publishRecord.version),
    };
  },

  // Non-persisting compile pass — returns ALL diagnostics (incl. non-blocking warnings such as
  // edge type mismatches) so the editor can surface them live.
  async validateWorkflow(id: string, nodes: FlowNode[], edges: FlowEdge[]): Promise<CompilationDiagnostic[]> {
    const mappedNodes = nodes.map(n => ({
      id: typeof n.id === 'string' ? { value: n.id } : n.id,
      type: n.type,
      properties: {
        ...((n.data?.properties as Record<string, unknown>) || {}),
        _metadata: { x: n.position.x, y: n.position.y },
      },
    }));
    const mappedEdges = edges.map(e => ({
      id: e.id,
      from: typeof e.source === 'string' ? { value: e.source } : e.source,
      output: e.sourceHandle || 'result',
      to: typeof e.target === 'string' ? { value: e.target } : e.target,
      input: e.targetHandle || 'in',
    }));
    const response = await fetch(`${API_BASE}/workflows/${id}/validate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ nodes: mappedNodes, edges: mappedEdges }),
    });
    const data = await handleResponse<{
      diagnostics?: CompilationDiagnostic[];
      reactiveDiagnostics?: { severity: number | string; code: string; nodeId?: string | null; message: string }[];
    }>(response);
    const base = Array.isArray(data?.diagnostics) ? data!.diagnostics! : [];
    // Device-block graphs are validated by the reactive layer (dead-end wires, untargeted blocks), not
    // the control-flow compiler — fold those into the same diagnostics stream the editor already shows.
    const reactive: CompilationDiagnostic[] = Array.isArray(data?.reactiveDiagnostics)
      ? data!.reactiveDiagnostics!.map(r => ({
          severity: (typeof r.severity === 'string'
            ? r.severity
            : r.severity === 0 ? 'Error' : 'Warning') as CompilationDiagnostic['severity'],
          code: r.code,
          message: r.message,
          nodeId: r.nodeId ? ({ value: r.nodeId } as NodeId) : undefined,
        }))
      : [];
    return [...base, ...reactive];
  },

  async getCredentials(): Promise<unknown[]> {
    const response = await fetch(`${API_BASE}/credentials`);
    return await handleResponse<unknown[]>(response);
  },

  async saveCredential(id: string, name: string, value: string): Promise<unknown> {
    const response = await fetch(`${API_BASE}/credentials`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ id, name, value }),
    });
    return await handleResponse<unknown>(response);
  },

  async deleteCredential(id: string): Promise<void> {
    const response = await fetch(`${API_BASE}/credentials/${id}`, {
      method: 'DELETE',
    });
    await handleResponse<void>(response);
  },

  async getNotificationChannels(): Promise<NotificationChannel[]> {
    const response = await fetch(`${API_BASE}/notification-channels`);
    return await handleResponse<NotificationChannel[]>(response);
  },

  async saveNotificationChannel(
    id: string,
    name: string,
    type: NotificationChannelType,
    config: Record<string, unknown> | null,
    isDefaultFailureAlert: boolean,
  ): Promise<NotificationChannel> {
    const response = await fetch(`${API_BASE}/notification-channels`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      // config omitted (null) on edit keeps the stored secret untouched.
      body: JSON.stringify({ id, name, type, config, isDefaultFailureAlert }),
    });
    return await handleResponse<NotificationChannel>(response);
  },

  async deleteNotificationChannel(id: string): Promise<void> {
    const response = await fetch(`${API_BASE}/notification-channels/${id}`, {
      method: 'DELETE',
    });
    await handleResponse<void>(response);
  },

  async testNotificationChannel(id: string): Promise<{ success: boolean; error?: string }> {
    const response = await fetch(`${API_BASE}/notification-channels/${id}/test`, {
      method: 'POST',
    });
    return await handleResponse<{ success: boolean; error?: string }>(response);
  },

  // ---- External signal systems (provider config editor) ----------------------------------------
  // Returns null when no provider supports administration (the UI hides the section in that case).
  async getVersion(): Promise<VersionInfo> {
    const response = await fetch(`${API_BASE}/version`);
    return await handleResponse<VersionInfo>(response);
  },

  async getExternalSystemsDescriptor(): Promise<ProviderDescriptor | null> {
    const response = await fetch(`${API_BASE}/external-systems/descriptor`);
    if (response.status === 404) return null;
    return await handleResponse<ProviderDescriptor>(response);
  },

  async getExternalSystem(): Promise<ExternalSystemInfo> {
    const response = await fetch(`${API_BASE}/external-systems`);
    return await handleResponse<ExternalSystemInfo>(response);
  },

  async clearExternalSystemDiagnostics(): Promise<ExternalSystemInfo> {
    const response = await fetch(`${API_BASE}/external-systems/diagnostics`, { method: 'DELETE' });
    return await handleResponse<ExternalSystemInfo>(response);
  },

  async renameExternalSystem(name: string): Promise<ExternalSystemInfo> {
    const response = await fetch(`${API_BASE}/external-systems`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    });
    return await handleResponse<ExternalSystemInfo>(response);
  },

  async setExternalSystemOption(key: string, value: boolean): Promise<ExternalSystemInfo> {
    const response = await fetch(`${API_BASE}/external-systems/options`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key, value }),
    });
    return await handleResponse<ExternalSystemInfo>(response);
  },

  async upsertExternalTarget(edit: ExternalTargetEdit): Promise<ExternalTargetInfo> {
    const response = await fetch(`${API_BASE}/external-systems/targets`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      // password omitted/null on edit keeps the stored secret.
      body: JSON.stringify(edit),
    });
    return await handleResponse<ExternalTargetInfo>(response);
  },

  async deleteExternalTarget(targetId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/external-systems/targets/${encodeURIComponent(targetId)}`, {
      method: 'DELETE',
    });
    await handleResponse<void>(response);
  },

  async syncExternalTarget(targetId: string): Promise<ExternalTargetInfo> {
    const response = await fetch(`${API_BASE}/external-systems/targets/${encodeURIComponent(targetId)}/sync`, {
      method: 'POST',
    });
    return await handleResponse<ExternalTargetInfo>(response);
  },

  async testExternalTarget(candidate: ExternalTargetEdit): Promise<TargetStatus> {
    const response = await fetch(`${API_BASE}/external-systems/targets/test`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(candidate),
    });
    return await handleResponse<TargetStatus>(response);
  },

  async getNodePackages(): Promise<NodePackageSummary[]> {
    const response = await fetch(`${API_BASE}/node-packages`);
    return await handleResponse<NodePackageSummary[]>(response);
  },

  async loadNodeOptions(
    integrationType: string,
    loaderName: string,
    body: { connectionId?: string | null; dependsOn?: Record<string, string>; search?: string; page?: string },
    refresh = false,
  ): Promise<LoadOptionsResult> {
    const query = refresh ? '?refresh=1' : '';
    const response = await fetch(
      `${API_BASE}/integrations/${encodeURIComponent(integrationType)}/options/${encodeURIComponent(loaderName)}${query}`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      },
    );
    return await handleResponse<LoadOptionsResult>(response);
  },

  async testInlineCode(code: string, language: string, inputs: Record<string, unknown>): Promise<InlineCodeTestResult> {
    const response = await fetch(`${API_BASE}/inline-code/test`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ code, language, inputs }),
    });
    return await handleResponse<InlineCodeTestResult>(response);
  },

  async testNodePackage(packageId: string, manifestYaml: string, executorCode: string, testsYaml: string): Promise<unknown> {
    const response = await fetch(`${API_BASE}/node-editor/test`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ packageId, manifestYaml, executorCode, testsYaml }),
    });
    return await handleResponse<unknown>(response);
  },

  async publishNodePackage(formData: FormData): Promise<unknown> {
    const response = await fetch(`${API_BASE}/node-packages/publish`, {
      method: 'POST',
      body: formData,
    });
    return await handleResponse<unknown>(response);
  },

  // Credentials (id + name only) for binding bundle credential slots.
  async listCredentials(): Promise<CredentialSummary[]> {
    const raw = await this.getCredentials();
    return asArray(raw)
      .filter(isRecord)
      .filter((c): c is { id: string; name: unknown } => typeof c.id === 'string')
      .map((c) => ({ id: c.id, name: getString(c.name, c.id) }));
  },

  // Export a manifest to a .kgbundle blob. The export endpoint returns
  // application/zip, so it can't use the JSON handleResponse helper — read the
  // body as a Blob and let the caller trigger the download. A bad manifest is a
  // 400 carrying { message }, which handleResponse turns into an ApiError.
  async exportBundle(manifest: BundleManifestInput): Promise<Blob> {
    const response = await fetch(`${API_BASE}/bundles/export`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(manifest),
    });
    if (!response.ok) {
      await handleResponse<unknown>(response); // throws ApiError with the server message
    }
    return await response.blob();
  },

  // Install a .kgbundle. The full verification report is returned on success
  // (200) AND on rejection (422 = verification gate, 409 = version conflict), so
  // those three statuses are surfaced as results — not thrown — for the UI to
  // render. Other statuses (400 malformed / 5xx) throw an ApiError.
  async installBundle(
    file: File,
    opts: { allowProvisional?: boolean; credentialBindings?: Record<string, string>; acknowledgePrivileged?: boolean } = {},
  ): Promise<{ status: number; result: BundleInstallResponse }> {
    const formData = new FormData();
    formData.append('bundle', file);
    if (opts.allowProvisional) {
      formData.append('allowProvisional', 'true');
    }
    if (opts.acknowledgePrivileged) {
      formData.append('acknowledgePrivileged', 'true');
    }
    const bindings = opts.credentialBindings ?? {};
    if (Object.keys(bindings).length > 0) {
      formData.append('credentialBindings', JSON.stringify(bindings));
    }

    const response = await fetch(`${API_BASE}/bundles/install`, {
      method: 'POST',
      body: formData,
    });

    if (response.status === 200 || response.status === 409 || response.status === 422) {
      const result = (await response.json()) as BundleInstallResponse;
      return { status: response.status, result };
    }

    await handleResponse<unknown>(response); // 400/5xx — throws ApiError with { message }
    throw new ApiError('Bundle install failed.', response.status, null);
  },

  // ── Templates (.kgtpl) ───────────────────────────────────────────────────

  // Export a workflow as a portable template. Returns the .kgtpl blob, a suggested
  // filename, and the portabilization report (which credential refs became slots),
  // surfaced via the X-Template-Portabilization response header.
  async exportTemplate(
    request: TemplateExportRequest,
  ): Promise<{ blob: Blob; filename: string; report: TemplatePortabilizationReport | null }> {
    const response = await fetch(`${API_BASE}/templates/export`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
    if (!response.ok) {
      await handleResponse<unknown>(response); // throws ApiError with the server message
    }

    // Parse Content-Disposition robustly: prefer the RFC 5987 `filename*=UTF-8''<pct-encoded>`
    // form and decode it; otherwise take plain `filename=` up to the next `;` (never swallowing a
    // trailing `filename*=…` segment — that was the bug behind names like "foo.kgtpl; filename*=…").
    const disposition = response.headers.get('Content-Disposition') ?? '';
    const extStar = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(disposition);
    const plain = /filename="([^"]+)"|filename=([^;]+)/i.exec(disposition);
    let filename = `${request.workflowId}.kgtpl`;
    if (extStar?.[1]) {
      try { filename = decodeURIComponent(extStar[1].trim()); } catch { filename = extStar[1].trim(); }
    } else if (plain) {
      filename = (plain[1] ?? plain[2] ?? '').trim();
    }

    let report: TemplatePortabilizationReport | null = null;
    const reportHeader = response.headers.get('X-Template-Portabilization');
    if (reportHeader) {
      try {
        report = JSON.parse(reportHeader) as TemplatePortabilizationReport;
      } catch {
        report = null;
      }
    }

    return { blob: await response.blob(), filename, report };
  },

  // Inspect an uploaded .kgtpl without importing: returns the manifest, declared
  // credential slots, and engine compatibility so the UI can bind before committing.
  async inspectTemplate(file: File): Promise<TemplateInspectResponse> {
    const formData = new FormData();
    formData.append('template', file);
    const response = await fetch(`${API_BASE}/templates/inspect`, { method: 'POST', body: formData });
    return await handleResponse<TemplateInspectResponse>(response);
  },

  // Install an uploaded .kgtpl as a new draft workflow, binding any declared slots. An optional
  // workflowName overrides the template name; the server collision-suffixes it ("… (2)") if taken.
  async installTemplate(
    file: File,
    credentialBindings: Record<string, string> = {},
    workflowName?: string,
    parameterValues: ParameterValues = {},
  ): Promise<TemplateInstallResponse> {
    const formData = new FormData();
    formData.append('template', file);
    if (Object.keys(credentialBindings).length > 0) {
      formData.append('credentialBindings', JSON.stringify(credentialBindings));
    }
    if (Object.keys(parameterValues).length > 0) {
      formData.append('parameterValues', JSON.stringify(parameterValues));
    }
    if (workflowName && workflowName.trim()) {
      formData.append('workflowName', workflowName.trim());
    }
    const response = await fetch(`${API_BASE}/templates/install`, { method: 'POST', body: formData });
    return await handleResponse<TemplateInstallResponse>(response);
  },

  // Read a template's node/edge graph (+ slots + compatibility) WITHOUT creating a workflow,
  // for inserting it into the currently open workflow. Declared parameters are substituted server-side.
  async getTemplatePayload(file: File, parameterValues: ParameterValues = {}): Promise<TemplatePayloadResponse> {
    const formData = new FormData();
    formData.append('template', file);
    if (Object.keys(parameterValues).length > 0) {
      formData.append('parameterValues', JSON.stringify(parameterValues));
    }
    const response = await fetch(`${API_BASE}/templates/payload`, { method: 'POST', body: formData });
    const raw = await handleResponse<Record<string, unknown>>(response);
    return mapTemplatePayload(raw);
  },

  async getGalleryTemplatePayload(templateId: string, parameterValues: ParameterValues = {}): Promise<TemplatePayloadResponse> {
    const query = Object.keys(parameterValues).length > 0
      ? `?parameterValues=${encodeURIComponent(JSON.stringify(parameterValues))}`
      : '';
    const response = await fetch(`${API_BASE}/templates/gallery/${encodeURIComponent(templateId)}/payload${query}`);
    const raw = await handleResponse<Record<string, unknown>>(response);
    return mapTemplatePayload(raw);
  },

  async listGalleryTemplates(): Promise<GalleryTemplate[]> {
    const response = await fetch(`${API_BASE}/templates/gallery`);
    return await handleResponse<GalleryTemplate[]>(response);
  },

  async getGalleryTemplate(templateId: string): Promise<GalleryTemplate> {
    const response = await fetch(`${API_BASE}/templates/gallery/${encodeURIComponent(templateId)}`);
    return await handleResponse<GalleryTemplate>(response);
  },

  // Install a built-in gallery template by id, binding any declared slots + supplying parameters.
  async installGalleryTemplate(
    templateId: string,
    credentialBindings: Record<string, string> = {},
    workflowName?: string,
    parameterValues: ParameterValues = {},
  ): Promise<TemplateInstallResponse> {
    const formData = new FormData();
    if (Object.keys(credentialBindings).length > 0) {
      formData.append('credentialBindings', JSON.stringify(credentialBindings));
    }
    if (Object.keys(parameterValues).length > 0) {
      formData.append('parameterValues', JSON.stringify(parameterValues));
    }
    if (workflowName && workflowName.trim()) {
      formData.append('workflowName', workflowName.trim());
    }
    const response = await fetch(
      `${API_BASE}/templates/gallery/${encodeURIComponent(templateId)}/install`,
      { method: 'POST', body: formData },
    );
    return await handleResponse<TemplateInstallResponse>(response);
  },

  // ── User template library (persisted) ────────────────────────────────────

  // Pack the given workflow and save it into this instance's library (upsert by template id).
  async saveTemplateToLibrary(request: TemplateExportRequest): Promise<GalleryTemplate> {
    const response = await fetch(`${API_BASE}/templates/library/save`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
    return await handleResponse<GalleryTemplate>(response);
  },

  async listLibraryTemplates(): Promise<GalleryTemplate[]> {
    const response = await fetch(`${API_BASE}/templates/library`);
    return await handleResponse<GalleryTemplate[]>(response);
  },

  // Save an uploaded .kgtpl directly into the library (no install), e.g. from the Import tab.
  async saveArchiveToLibrary(file: File): Promise<GalleryTemplate> {
    const formData = new FormData();
    formData.append('template', file);
    const response = await fetch(`${API_BASE}/templates/library/save-archive`, { method: 'POST', body: formData });
    return await handleResponse<GalleryTemplate>(response);
  },

  async getLibraryTemplatePayload(templateId: string, parameterValues: ParameterValues = {}): Promise<TemplatePayloadResponse> {
    const query = Object.keys(parameterValues).length > 0
      ? `?parameterValues=${encodeURIComponent(JSON.stringify(parameterValues))}`
      : '';
    const response = await fetch(`${API_BASE}/templates/library/${encodeURIComponent(templateId)}/payload${query}`);
    const raw = await handleResponse<Record<string, unknown>>(response);
    return mapTemplatePayload(raw);
  },

  async installLibraryTemplate(
    templateId: string,
    credentialBindings: Record<string, string> = {},
    workflowName?: string,
    parameterValues: ParameterValues = {},
  ): Promise<TemplateInstallResponse> {
    const formData = new FormData();
    if (Object.keys(credentialBindings).length > 0) {
      formData.append('credentialBindings', JSON.stringify(credentialBindings));
    }
    if (Object.keys(parameterValues).length > 0) {
      formData.append('parameterValues', JSON.stringify(parameterValues));
    }
    if (workflowName && workflowName.trim()) {
      formData.append('workflowName', workflowName.trim());
    }
    const response = await fetch(
      `${API_BASE}/templates/library/${encodeURIComponent(templateId)}/install`,
      { method: 'POST', body: formData },
    );
    return await handleResponse<TemplateInstallResponse>(response);
  },

  async deleteLibraryTemplate(templateId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/templates/library/${encodeURIComponent(templateId)}`, { method: 'DELETE' });
    await handleResponse<{ removed: boolean }>(response);
  },

  async getGroups(): Promise<{ container: WorkflowGroupContainer; etag: string }> {
    const response = await fetch(`${API_BASE}/workflow-groups`);
    const etag = response.headers.get('ETag') || '';
    const container = await handleResponse<WorkflowGroupContainer>(response);
    return { container, etag };
  },

  async saveGroups(container: WorkflowGroupContainer, etag: string): Promise<string> {
    const response = await fetch(`${API_BASE}/workflow-groups`, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        'If-Match': etag,
      },
      body: JSON.stringify(container),
    });
    
    await handleResponse<unknown>(response);
    return response.headers.get('ETag') || '';
  },

  async deleteGroup(id: string): Promise<void> {
    const response = await fetch(`${API_BASE}/workflow-groups/${id}`, {
      method: 'DELETE',
    });
    await handleResponse<void>(response);
  },

  // ── Backup & Restore (.kgbak) ────────────────────────────────────────────

  // Produce an encrypted backup. Two protection modes: a passphrase (portable) or
  // this server's key (`useServerKey`, no passphrase, restorable only on this host).
  // The endpoint returns application/octet-stream, so this reads the body as a Blob
  // (not the JSON handleResponse helper). A 400 carries { message } → ApiError.
  async createBackup(
    opts: { passphrase?: string; includeRunHistory?: boolean; useServerKey?: boolean },
  ): Promise<{ blob: Blob; filename: string }> {
    const response = await fetch(`${API_BASE}/admin/backup`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        passphrase: opts.passphrase ?? null,
        includeRunHistory: opts.includeRunHistory ?? false,
        useServerKey: opts.useServerKey ?? false,
      }),
    });
    if (!response.ok) {
      await handleResponse<unknown>(response); // throws ApiError with the server message
    }
    const blob = await response.blob();
    const disposition = response.headers.get('Content-Disposition') ?? '';
    const match = /filename\*?=(?:UTF-8'')?["']?([^"';]+)/i.exec(disposition);
    const filename = match ? decodeURIComponent(match[1]) : 'knotarium-backup.kgbak';
    return { blob, filename };
  },

  // Preview a backup without writing anything. 200 → the manifest. A wrong
  // passphrase (400) or an incompatible format (409, body carries { manifest })
  // throws an ApiError the caller can branch on by `status`.
  async inspectBackup(file: File, passphrase: string): Promise<BackupManifest> {
    const formData = new FormData();
    formData.append('backup', file);
    formData.append('passphrase', passphrase);
    const response = await fetch(`${API_BASE}/admin/backup/inspect`, { method: 'POST', body: formData });
    return await handleResponse<BackupManifest>(response);
  },

  // DESTRUCTIVE full-instance restore. `confirm` must be true. 200 → the report.
  // The guards surface as ApiError: 412 (runtime armed), 422 (not confirmed),
  // 409 (incompatible format), 400 (bad passphrase / malformed) — the caller
  // branches on `status` to render the right message.
  async restoreBackup(file: File, passphrase: string, confirm: boolean): Promise<RestoreReport> {
    const formData = new FormData();
    formData.append('backup', file);
    formData.append('passphrase', passphrase);
    formData.append('confirm', confirm ? 'true' : 'false');
    const response = await fetch(`${API_BASE}/admin/restore`, { method: 'POST', body: formData });
    return await handleResponse<RestoreReport>(response);
  },

  // ── Vendor-setting import (plugin-contributed providers) ──────────────────

  // List the import providers a host plugin has registered (e.g. a vendor .set reader).
  async listImportProviders(): Promise<ImportProviderDescriptor[]> {
    const response = await fetch(`${API_BASE}/imports/providers`);
    return await handleResponse<ImportProviderDescriptor[]>(response);
  },

  // Preview an upload: returns the generated-workflow summaries, coverage report, discovered servers, and
  // (given a strategy) the target plan. Installs/provisions nothing.
  async previewImport(
    providerId: string,
    file: File,
    granularity: ImportGranularity,
    targetStrategy?: ImportTargetStrategy,
    serverMappings?: Record<string, string>,
  ): Promise<ImportPreviewResponse> {
    const formData = new FormData();
    formData.append('file', file);
    formData.append('granularity', granularity);
    if (targetStrategy) formData.append('targetStrategy', targetStrategy);
    if (serverMappings && Object.keys(serverMappings).length > 0) formData.append('serverMappings', JSON.stringify(serverMappings));
    const response = await fetch(`${API_BASE}/imports/${encodeURIComponent(providerId)}/preview`, { method: 'POST', body: formData });
    return await handleResponse<ImportPreviewResponse>(response);
  },

  // Install an upload as inactive Imported workflow versions and provision its device connections per the chosen
  // strategy; returns what was created/installed + the report.
  async installImport(
    providerId: string,
    file: File,
    granularity: ImportGranularity,
    targetStrategy?: ImportTargetStrategy,
    serverMappings?: Record<string, string>,
  ): Promise<ImportInstallResponse> {
    const formData = new FormData();
    formData.append('file', file);
    formData.append('granularity', granularity);
    if (targetStrategy) formData.append('targetStrategy', targetStrategy);
    if (serverMappings && Object.keys(serverMappings).length > 0) formData.append('serverMappings', JSON.stringify(serverMappings));
    const response = await fetch(`${API_BASE}/imports/${encodeURIComponent(providerId)}/install`, { method: 'POST', body: formData });
    return await handleResponse<ImportInstallResponse>(response);
  },
};

