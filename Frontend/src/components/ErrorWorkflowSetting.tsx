// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import type { WorkflowDefinition } from '../types';
import { api } from '../utils/api';

const inputStyle: React.CSSProperties = {
  width: '100%',
  maxWidth: '420px',
  padding: '10px',
  borderRadius: '8px',
  background: 'rgba(0, 0, 0, 0.2)',
  border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
  color: '#fff',
  fontSize: '0.85rem',
  outline: 'none',
  boxSizing: 'border-box',
};

/**
 * Global "error workflow" picker: selects the single workflow that is started automatically
 * whenever any other workflow fails. Persists via PUT /api/settings/error-workflow.
 */
export function ErrorWorkflowSetting() {
  const [workflows, setWorkflows] = useState<WorkflowDefinition[]>([]);
  const [selectedId, setSelectedId] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedNote, setSavedNote] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    Promise.all([api.getWorkflows(), api.getDefaultErrorWorkflow()])
      .then(([wfs, current]) => {
        setWorkflows(wfs);
        setSelectedId(current ?? '');
      })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load error workflow setting.'))
      .finally(() => setLoading(false));
  }, []);

  const handleChange = async (value: string) => {
    setSelectedId(value);
    setSaving(true);
    setError(null);
    setSavedNote(null);
    try {
      const saved = await api.setDefaultErrorWorkflow(value === '' ? null : value);
      setSelectedId(saved ?? '');
      setSavedNote(saved ? 'Saved.' : 'Cleared.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save error workflow setting.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div
      style={{
        padding: '20px',
        borderRadius: '12px',
        background: 'rgba(255, 255, 255, 0.03)',
        border: '1px solid var(--border-color, rgba(255, 255, 255, 0.1))',
        marginBottom: '24px',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '6px' }}>
        <AlertTriangle size={16} style={{ color: '#f59e0b' }} />
        <h3 style={{ margin: 0, fontSize: '0.95rem', color: '#fff' }}>Error Workflow</h3>
      </div>
      <p style={{ margin: '0 0 14px', fontSize: '0.8rem', color: 'var(--text-secondary, #94a3b8)' }}>
        Run this workflow automatically whenever any other workflow fails. It receives the failure
        context (workflow, failed node, error) on its Error Trigger node. The error workflow itself is
        never re-triggered by its own failures.
      </p>

      {loading ? (
        <div style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)' }}>Loading…</div>
      ) : (
        <select
          value={selectedId}
          disabled={saving}
          onChange={(e) => handleChange(e.target.value)}
          style={inputStyle}
        >
          <option value="">— None (disabled) —</option>
          {workflows.map((wf) => (
            <option key={wf.id.value} value={wf.id.value}>
              {wf.name || wf.id.value}
            </option>
          ))}
        </select>
      )}

      {savedNote && (
        <span style={{ marginLeft: '10px', fontSize: '0.78rem', color: '#34d399' }}>{savedNote}</span>
      )}
      {error && (
        <div style={{ marginTop: '10px', fontSize: '0.8rem', color: '#f87171' }}>{error}</div>
      )}
    </div>
  );
}
