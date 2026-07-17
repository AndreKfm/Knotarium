// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { VariableType } from '../components/VariableToken';
import type { WorkflowDefinition } from '../types';

export interface SubflowVariable {
  name: string;
  type: VariableType;
}

export interface SubflowInterface {
  inputs: SubflowVariable[];
  outputs: SubflowVariable[];
}

// Read a workflow's declared interface (input locals on its Start node, output locals on its End
// node), so a subflow node can render one bind-slot per declared local of the child it calls.
export function extractSubflowInterface(workflow: WorkflowDefinition): SubflowInterface {
  const toVars = (raw: unknown): SubflowVariable[] => {
    if (!Array.isArray(raw)) return [];
    const result: SubflowVariable[] = [];
    for (const entry of raw) {
      if (entry && typeof entry === 'object') {
        const row = entry as { name?: unknown; type?: unknown };
        if (typeof row.name === 'string' && row.name.length > 0) {
          const type = (row.type === 'number' || row.type === 'boolean' || row.type === 'object') ? row.type : 'string';
          result.push({ name: row.name, type });
        }
      }
    }
    return result;
  };
  const startNode = workflow.nodes.find((n) => n.type === 'start');
  const endNode = workflow.nodes.find((n) => n.type === 'end');
  return {
    inputs: toVars(startNode?.properties?.interfaceInputs),
    outputs: toVars(endNode?.properties?.interfaceOutputs),
  };
}
