// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { ExecutionStatus } from '../../types';

export interface ExecutionDetailProps {
  executionId: string;
  onBack: () => void;
  onTriggeredExecution: (executionId: string) => void;
  /** Navigate to Settings → File Access (used by the "Grant this path" CTA on a File Access denial). */
  onGrantFileAccess?: () => void;
}

export type VisualRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Waiting' | 'Retrying' | 'Cancelled';
export type VisualNodeStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Waiting' | 'Retrying' | 'RequiresManualDecision' | 'Cancelled' | 'Skipped' | 'Triggered';
export type KnownExecutionStatus = ExecutionStatus | VisualRunStatus | VisualNodeStatus;

export type JournalOverviewEntry = {
  id: string;
  eventType: string;
  message: string;
  offsetLabel: string;
  /** Absolute clock time this event occurred (e.g. "07:44:34"), or null if unknown. */
  clockLabel: string | null;
  status: KnownExecutionStatus;
  data: Record<string, unknown>;
};

export type JournalOverviewGroup = {
  key: string;
  nodeId?: string;
  nodeType?: string;
  title: string;
  subtitle: string;
  hint: string | null;
  status: KnownExecutionStatus;
  /** Offset of this node's last event from the run start (e.g. "+1ms"). */
  durationLabel: string;
  /** Absolute clock time this node started (e.g. "22:04:53"), or null if unknown. */
  startedAtLabel?: string | null;
  /** How long this node itself ran, last event − start (e.g. "4 ms"), or undefined if not measurable. */
  runDurationLabel?: string;
  entries: JournalOverviewEntry[];
  isWorkflow: boolean;
  // True when this entry is a node that ran inside an inlined subflow (its id is prefixed with the
  // subflow node id, e.g. `subflow-abc/log-xyz`). Used to badge/colour it distinctly in the timeline.
  isSubflowChild?: boolean;
  latestPayload?: Record<string, unknown>;
};