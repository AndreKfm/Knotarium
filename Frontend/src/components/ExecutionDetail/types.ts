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
  durationLabel: string;
  entries: JournalOverviewEntry[];
  isWorkflow: boolean;
  // True when this entry is a node that ran inside an inlined subflow (its id is prefixed with the
  // subflow node id, e.g. `subflow-abc/log-xyz`). Used to badge/colour it distinctly in the timeline.
  isSubflowChild?: boolean;
  latestPayload?: Record<string, unknown>;
};