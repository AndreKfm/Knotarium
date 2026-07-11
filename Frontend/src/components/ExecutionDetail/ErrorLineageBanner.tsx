import { AlertTriangle, ArrowRight, ShieldCheck } from 'lucide-react';
import type { HandlerRun } from './useHandlerRun';

interface ErrorLineageBannerProps {
  /** Set when THIS run is an error-handler run — links back to the failed run it handles. */
  errorOfExecutionId?: string;
  /** The error-handler run spawned by THIS (failed) run, if any. */
  handlerRun?: HandlerRun | null;
  /** Navigate to another execution (used for the backward link). */
  onOpen: (executionId: string) => void;
  /** Open the handler-run drawer (the demoted, secondary forward entry point). */
  onOpenHandler: () => void;
}

// Coral outline action — mirrors the amber button's size/prominence but reads as the failure side.
const backPill: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 8,
  padding: '8px 17px', borderRadius: 999,
  background: 'rgba(242, 90, 85, 0.10)', border: '1px solid rgba(242, 120, 116, 0.55)',
  color: '#f9a8a4', cursor: 'pointer', fontSize: '0.82rem', fontWeight: 700,
  boxShadow: '0 0 18px rgba(242, 90, 85, 0.20)',
};

// Solid amber action: reads unmistakably as a button and as "something was handled".
const actionPill: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 8,
  padding: '8px 17px', borderRadius: 999,
  background: 'linear-gradient(180deg, #f8c34e, #f5a623)', border: 'none',
  color: '#231603', cursor: 'pointer', fontSize: '0.82rem', fontWeight: 800,
  boxShadow: '0 0 22px rgba(245, 166, 35, 0.45)',
};

/**
 * Top-bar lineage entries:
 * - backward (on an error-handler run): "Triggered by failed run →" navigates to the failed run.
 * - forward (on a failed run): a solid amber "Error handler ran →" action that opens the handler run.
 */
export function ErrorLineageBanner({ errorOfExecutionId, handlerRun, onOpen, onOpenHandler }: ErrorLineageBannerProps) {
  if (!errorOfExecutionId && !handlerRun) {
    return null;
  }

  return (
    <div style={{ position: 'absolute', top: 14, left: '50%', transform: 'translateX(-50%)', zIndex: 20, display: 'flex', gap: 8, alignItems: 'center' }}>
      {errorOfExecutionId && (
        <button style={backPill} onClick={() => onOpen(errorOfExecutionId)} title="Open the workflow run that failed and triggered this handler">
          <AlertTriangle size={15} /> Triggered by failed run <ArrowRight size={15} />
        </button>
      )}
      {handlerRun && (
        <button style={actionPill} onClick={onOpenHandler} title="Open the error-handler run that caught this failure">
          <ShieldCheck size={15} /> Error handler ran <ArrowRight size={15} />
        </button>
      )}
    </div>
  );
}
