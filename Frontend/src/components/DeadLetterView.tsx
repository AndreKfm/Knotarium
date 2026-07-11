import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, RefreshCw, Trash2, ExternalLink } from 'lucide-react';
import type { ExecutionInstance } from '../types';
import { api } from '../utils/api';

interface DeadLetterViewProps {
  onOpenExecution: (executionId: string) => void;
}

/**
 * Dead-letter store: the failed runs that need triage. Each can be opened (to inspect / replay via
 * the execution detail view) or discarded (drops it to the Discarded status, off this list).
 */
export function DeadLetterView({ onOpenExecution }: DeadLetterViewProps) {
  const [executions, setExecutions] = useState<ExecutionInstance[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(() => {
    setLoading(true);
    setError(null);
    api.getExecutions({ status: 'Failed' })
      .then(setExecutions)
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load failed executions.'))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const handleDiscard = async (id: string) => {
    setBusyId(id);
    setError(null);
    try {
      await api.discardExecution(id);
      setExecutions((prev) => prev.filter((e) => e.id !== id));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to discard execution.');
    } finally {
      setBusyId(null);
    }
  };

  return (
    <div style={{ padding: '24px', maxWidth: '1000px', margin: '0 auto' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: '6px' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
          <AlertTriangle size={20} style={{ color: '#f87171' }} />
          <h2 style={{ margin: 0, fontSize: '1.2rem', color: '#fff' }}>Dead Letter</h2>
        </div>
        <button
          onClick={load}
          style={{
            display: 'flex', alignItems: 'center', gap: '6px', padding: '8px 12px',
            background: 'transparent', border: '1px solid var(--border-color, rgba(255,255,255,0.1))',
            borderRadius: '8px', color: 'var(--text-secondary, #94a3b8)', cursor: 'pointer', fontSize: '0.8rem',
          }}
        >
          <RefreshCw size={14} /> Refresh
        </button>
      </div>
      <p style={{ margin: '0 0 18px', fontSize: '0.82rem', color: 'var(--text-secondary, #94a3b8)' }}>
        Failed workflow runs awaiting triage. Open a run to inspect or replay it, or discard it once handled.
      </p>

      {error && <div style={{ marginBottom: '12px', fontSize: '0.82rem', color: '#f87171' }}>{error}</div>}

      {loading ? (
        <div style={{ fontSize: '0.85rem', color: 'var(--text-secondary, #94a3b8)' }}>Loading…</div>
      ) : executions.length === 0 ? (
        <div
          style={{
            padding: '32px', textAlign: 'center', borderRadius: '12px',
            background: 'rgba(255,255,255,0.03)', border: '1px solid var(--border-color, rgba(255,255,255,0.1))',
            color: 'var(--text-secondary, #94a3b8)', fontSize: '0.9rem',
          }}
        >
          No failed runs. 🎉
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
          {executions.map((exec) => (
            <div
              key={exec.id}
              style={{
                display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '12px',
                padding: '14px 16px', borderRadius: '10px',
                background: 'rgba(248, 113, 113, 0.06)',
                border: '1px solid rgba(248, 113, 113, 0.2)',
              }}
            >
              <div style={{ minWidth: 0 }}>
                <div style={{ fontSize: '0.9rem', color: '#fff', fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {exec.workflowName || exec.workflowDefinitionId.value}
                </div>
                <div style={{ fontSize: '0.75rem', color: 'var(--text-secondary, #94a3b8)', marginTop: '2px' }}>
                  {exec.triggerOrigin ? `${exec.triggerOrigin} · ` : ''}{new Date(exec.updatedAt).toLocaleString()}
                </div>
              </div>
              <div style={{ display: 'flex', gap: '8px', flexShrink: 0 }}>
                <button
                  onClick={() => onOpenExecution(exec.id)}
                  style={actionBtnStyle}
                >
                  <ExternalLink size={14} /> Open
                </button>
                <button
                  onClick={() => handleDiscard(exec.id)}
                  disabled={busyId === exec.id}
                  style={{ ...actionBtnStyle, color: '#f87171', borderColor: 'rgba(248,113,113,0.3)' }}
                >
                  <Trash2 size={14} /> Discard
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

const actionBtnStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: '6px', padding: '7px 12px',
  background: 'transparent', border: '1px solid var(--border-color, rgba(255,255,255,0.1))',
  borderRadius: '8px', color: 'var(--text-secondary, #cbd5e1)', cursor: 'pointer', fontSize: '0.78rem',
};
