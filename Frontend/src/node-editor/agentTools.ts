// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Pure helpers for the AI Agent node's `tools` property: a list of tool bindings, each pointing at a
// workflow the agent may call as a tool. This mirrors the backend contract in AiAgentNodeTask.TryParseTools
// / IsValidToolName (Backend/Knotarium.Features/Nodes/AiAgentNodeTask.cs) — the editor validates the same
// rules so a node the UI accepts is a node the backend accepts. Keep the two in sync.

export type ToolParameterType = 'string' | 'number' | 'boolean';

export interface ToolParameter {
  name: string;
  type: ToolParameterType;
  required: boolean;
  description?: string;
}

export interface AgentToolBinding {
  /** Target workflow definition id (WorkflowDefinition.id.value). */
  workflowId: string;
  /** Model-facing tool name: [a-zA-Z0-9_]{1,64}. */
  name: string;
  /** The model's only guidance on when to use the tool. */
  description: string;
  parameters: ToolParameter[];
  /** Global-variable names projected out of the finished run as the tool result. */
  outputs: string[];
}

const TOOL_NAME_RE = /^[a-zA-Z0-9_]{1,64}$/;

/** Mirrors AiAgentNodeTask.IsValidToolName. */
export function isValidToolName(name: string): boolean {
  return TOOL_NAME_RE.test(name);
}

function normalizeType(type: unknown): ToolParameterType {
  const t = String(type ?? '').trim().toLowerCase();
  if (t === 'number' || t === 'integer') return 'number';
  if (t === 'boolean' || t === 'bool') return 'boolean';
  return 'string';
}

function normalizeParameter(raw: unknown): ToolParameter | null {
  // Drop only non-object entries; keep empty-named rows so the editor can hold an in-progress parameter
  // the user is still typing. The backend ignores empty-named params (AiAgentNodeTask.TryParseTools), so
  // an unfinished row that reaches the stored value is harmless.
  if (typeof raw !== 'object' || raw === null) return null;
  const p = raw as Record<string, unknown>;
  return {
    name: typeof p.name === 'string' ? p.name : '',
    type: normalizeType(p.type),
    required: p.required === true,
    description: typeof p.description === 'string' ? p.description : undefined,
  };
}

function normalizeBinding(raw: unknown): AgentToolBinding | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const b = raw as Record<string, unknown>;
  const parameters = Array.isArray(b.parameters)
    ? b.parameters.map(normalizeParameter).filter((p): p is ToolParameter => p !== null)
    : [];
  const outputs = Array.isArray(b.outputs)
    ? b.outputs.map((o) => (typeof o === 'string' ? o.trim() : '')).filter((o) => o.length > 0)
    : [];
  return {
    workflowId: typeof b.workflowId === 'string' ? b.workflowId : '',
    name: typeof b.name === 'string' ? b.name : '',
    description: typeof b.description === 'string' ? b.description : '',
    parameters,
    outputs,
  };
}

/**
 * Reads the stored `tools` property into a normalized binding array. Tolerates a live JS array and a
 * legacy JSON string (the backend RawJson reader accepts both); anything else yields an empty list.
 */
export function readToolBindings(value: unknown): AgentToolBinding[] {
  let arr: unknown = value;
  if (typeof value === 'string') {
    if (value.trim() === '') return [];
    try {
      arr = JSON.parse(value);
    } catch {
      return [];
    }
  }
  if (!Array.isArray(arr)) return [];
  return arr.map(normalizeBinding).filter((b): b is AgentToolBinding => b !== null);
}

/**
 * Returns human-readable problems that would make the backend reject the tools list
 * (mirrors AiAgentNodeTask.TryParseTools). Empty array = the list is valid.
 */
export function validateToolBindings(bindings: AgentToolBinding[]): string[] {
  const problems: string[] = [];
  const seen = new Set<string>();
  bindings.forEach((b, i) => {
    const label = b.name || `tool #${i + 1}`;
    if (!b.name || !isValidToolName(b.name)) {
      problems.push(`Tool name "${b.name}" is invalid (letters, digits, underscore; 1–64 chars).`);
    } else if (seen.has(b.name)) {
      problems.push(`Duplicate tool name "${b.name}".`);
    } else {
      seen.add(b.name);
    }
    if (!b.workflowId) {
      problems.push(`Tool "${label}" has no target workflow.`);
    }
  });
  return problems;
}

export function emptyBinding(): AgentToolBinding {
  return { workflowId: '', name: '', description: '', parameters: [], outputs: [] };
}
