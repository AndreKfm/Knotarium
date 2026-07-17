// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { api } from '../utils/api';
import type { ExecutionInstance, NodeManifest, NodeState } from '../types';
import { NodeIoPanels } from './shared/NodeIoPanels';

interface NodeIoInspectorProps {
  workflowId: string | null;
  nodeId: string;
  manifest: NodeManifest | null;
}

/**
 * Editor-side per-node input/output inspector. Fetches the workflow's most recent run and shows the
 * selected node's recorded Inputs / Variables / Outputs — the design-canvas counterpart of the
 * run-view time-travel inspector. Falls back to the manifest's declared output schema (from the typed
 * manifest surfaced in step 1a) before the node has ever run, so authors can still see what a node emits.
 */
export function NodeIoInspector({ workflowId, nodeId, manifest }: NodeIoInspectorProps) {
  const [execution, setExecution] = useState<ExecutionInstance | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    if (!workflowId) {
      setExecution(null);
      return;
    }
    setLoading(true);
    setError(null);
    api.getLatestExecution(workflowId)
      .then(setExecution)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
      .finally(() => setLoading(false));
  }, [workflowId]);

  useEffect(() => { load(); }, [load]);

  const nodeState: NodeState | undefined = execution?.nodeStates?.find((state) => state.nodeId.value === nodeId);
  const declaredOutputs = manifest?.outputs ?? [];

  return (
    <div style={{ marginTop: 16, borderTop: '1px solid var(--border-color)', paddingTop: 16, display: 'flex', flexDirection: 'column', gap: 10, minHeight: 220 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <span style={{ fontSize: '0.72rem', fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          Input / Output
        </span>
        <button
          onClick={load}
          disabled={!workflowId || loading}
          title="Refresh from the latest run"
          style={{ display: 'inline-flex', alignItems: 'center', gap: 5, padding: '3px 8px', borderRadius: 6, background: 'rgba(255,255,255,0.04)', border: '1px solid var(--border-color)', color: 'var(--text-secondary)', fontSize: '0.72rem', cursor: workflowId && !loading ? 'pointer' : 'default' }}
        >
          <RefreshCw size={12} style={loading ? { animation: 'spin 1s linear infinite' } : undefined} /> Refresh
        </button>
      </div>

      {error ? (
        <span style={{ fontSize: '0.78rem', color: 'var(--color-error)' }}>Could not load the latest run: {error}</span>
      ) : nodeState ? (
        <NodeIoPanels nodeState={nodeState} layout="column" />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <span style={{ fontSize: '0.78rem', color: 'var(--text-muted)', fontStyle: 'italic' }}>
            {loading ? 'Loading the latest run…' : 'This node has no recorded run yet — run the workflow to capture its input/output.'}
          </span>
          {declaredOutputs.length > 0 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <span style={{ fontSize: '0.68rem', textTransform: 'uppercase', letterSpacing: '0.08em', color: '#7dd3fc', fontWeight: 700 }}>
                Declared outputs
              </span>
              {declaredOutputs.map((output) => (
                <div key={output.name} style={{ fontFamily: 'monospace', fontSize: '0.76rem', color: '#e2e8f0' }}>
                  <span style={{ color: '#67e8f9' }}>{output.name}</span>
                  {output.type && <span style={{ color: '#94a3b8' }}>: {output.type}</span>}
                  {output.fields && output.fields.length > 0 && (
                    <span style={{ color: '#94a3b8' }}> {'{'} {output.fields.map((f) => f.name).join(', ')} {'}'}</span>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
