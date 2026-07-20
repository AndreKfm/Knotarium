// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// The API serializes the status enums by ORDINAL (System.Text.Json default — there is no global
// JsonStringEnumConverter on the API JSON), so a status arrives over the wire as a number. These
// decoders map that number back to the enum name the frontend works with. The arrays MUST stay in
// sync with the backend enum declarations. Strings pass through unchanged, so this is safe to apply
// unconditionally and defensive if the wire format ever switches to string enums.

// Backend/Knotarium.Core/Domain/NodeStatus.cs
const NODE_STATUS_NAMES = ['Pending', 'Running', 'Completed', 'Failed', 'Waiting', 'RequiresManualDecision'] as const;

// Backend ExecutionStatus (Pending=0 … Discarded=7)
const EXECUTION_STATUS_NAMES = ['Pending', 'Running', 'Suspended', 'Cancelled', 'Completed', 'Failed', 'WaitingForRetry', 'Discarded'] as const;

export function decodeNodeStatus(raw: unknown): string {
  if (typeof raw === 'number') {
    return NODE_STATUS_NAMES[raw] ?? 'Pending';
  }
  return typeof raw === 'string' && raw.length > 0 ? raw : 'Pending';
}

export function decodeExecutionStatus(raw: unknown): string {
  if (typeof raw === 'number') {
    return EXECUTION_STATUS_NAMES[raw] ?? 'Pending';
  }
  return typeof raw === 'string' && raw.length > 0 ? raw : 'Pending';
}
