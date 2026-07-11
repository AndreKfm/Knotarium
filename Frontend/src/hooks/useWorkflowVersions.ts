import { useCallback, useEffect, useState } from 'react';
import { api } from '../utils/api';
import type { WorkflowVersionSummary } from '../types';

export interface UseWorkflowVersionsResult {
  versions: WorkflowVersionSummary[];
  loading: boolean;
  error: string | null;
  /** Re-fetch the version list from the metadata endpoint. */
  refresh: () => Promise<void>;
}

/**
 * Thin hook over the metadata version-list endpoint (GET /api/workflows/{id}/versions).
 * Returns the version summaries (newest first), a loading flag, an error message,
 * and a {@link UseWorkflowVersionsResult.refresh} function the caller can invoke
 * after publishing a new version.
 *
 * Fetching is gated on `enabled` so the history panel only hits the API while it
 * is open. Passing a falsy `workflowId` yields an empty, idle result.
 */
export function useWorkflowVersions(
  workflowId: string | undefined,
  enabled = true,
): UseWorkflowVersionsResult {
  const [versions, setVersions] = useState<WorkflowVersionSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    if (!workflowId) {
      setVersions([]);
      return;
    }

    setLoading(true);
    setError(null);
    try {
      const items = await api.getWorkflowVersions(workflowId);
      setVersions(items);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load workflow versions.');
    } finally {
      setLoading(false);
    }
  }, [workflowId]);

  useEffect(() => {
    if (!enabled || !workflowId) {
      return;
    }
    void refresh();
  }, [enabled, workflowId, refresh]);

  return { versions, loading, error, refresh };
}
