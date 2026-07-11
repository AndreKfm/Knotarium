import { Handle, Position } from '@xyflow/react';
import { ShieldCheck, ArrowRight, CheckCircle2 } from 'lucide-react';

const AMBER = '#f5a623';

interface ErrorHandlerCardData {
  status?: string;
  onOpen?: () => void;
}

/**
 * Affordance A — the "catch branch" card on the canvas. A synthetic React Flow node that the failed
 * node connects to via an amber dashed edge, making the cause→handler relationship spatial. Clicking
 * it opens the shared handler-run drawer.
 */
export function ErrorHandlerCardNode({ data }: { data: ErrorHandlerCardData }) {
  const status = data?.status ?? '';
  const completed = status.toLowerCase() === 'completed';

  return (
    <div
      onClick={data?.onOpen}
      className="nodrag nopan"
      style={{
        width: 230, cursor: 'pointer', borderRadius: 14, padding: '12px 14px',
        background: 'linear-gradient(180deg, rgba(245,166,35,0.12), rgba(245,166,35,0.04))',
        border: `1.5px solid ${AMBER}88`,
        boxShadow: `0 0 18px ${AMBER}22`,
      }}
    >
      <Handle type="target" position={Position.Top} style={{ background: AMBER, border: 'none', width: 8, height: 8 }} />

      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 6 }}>
        <span style={{ fontSize: '0.6rem', fontWeight: 800, letterSpacing: '0.08em', color: AMBER }}>
          ON ERROR → CATCH
        </span>
        {completed && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: '0.62rem', fontWeight: 700, color: '#34d399' }}>
            <CheckCircle2 size={12} /> Completed
          </span>
        )}
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
        <ShieldCheck size={16} style={{ color: AMBER }} />
        <span style={{ color: '#fff', fontWeight: 700, fontSize: '0.92rem' }}>Error Handler</span>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'flex-end' }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: 5, color: AMBER, fontWeight: 700, fontSize: '0.78rem' }}>
          View run <ArrowRight size={14} />
        </span>
      </div>
    </div>
  );
}
