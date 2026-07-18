// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useRef, useState } from 'react';
import { api } from '../utils/api';
import { useScrimClose } from '../hooks/useScrimClose';
import type { WorkflowDefinition } from '../types';

interface AiGenerateModalProps {
  open: boolean;
  onClose: () => void;
  /** Called once a workflow is generated, with the definition and its unbound credential slots. */
  onGenerated: (workflow: WorkflowDefinition, openSlots: string[]) => void;
  /** When set, the dialog REFINES this open workflow per the intent instead of generating from scratch. */
  currentWorkflow?: WorkflowDefinition | null;
}

type Phase = 'input' | 'generating' | 'error';

const POLL_INTERVAL_MS = 1200;
const MAX_POLLS = 60; // ~72s ceiling for the repair loop

/**
 * "Generate with AI" dialog: capture an intent, start a generation job, and poll it. On success the
 * generated workflow is handed back to the host (which loads it onto the canvas as an unsaved preview);
 * on failure the compiler diagnostics or a configuration error are shown so the user can retry.
 */
export function AiGenerateModal({ open, onClose, onGenerated, currentWorkflow }: AiGenerateModalProps) {
  const refine = !!currentWorkflow;
  const [intent, setIntent] = useState('');
  const [phase, setPhase] = useState<Phase>('input');
  const [errorLines, setErrorLines] = useState<string[]>([]);
  const cancelledRef = useRef(false);

  // Reset when the dialog is reopened; stop any in-flight polling when it closes/unmounts.
  useEffect(() => {
    if (open) {
      cancelledRef.current = false;
      setPhase('input');
      setErrorLines([]);
    }
    return () => {
      cancelledRef.current = true;
    };
  }, [open]);

  // Backdrop dismiss + Esc — suspended while a generation is in flight.
  const onScrimMouseDown = useScrimClose(onClose, phase !== 'generating');

  if (!open) return null;

  const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

  async function generate() {
    const trimmed = intent.trim();
    if (trimmed.length === 0) return;

    setPhase('generating');
    setErrorLines([]);
    try {
      const { jobId } = await api.generateWorkflow(trimmed, currentWorkflow);

      for (let i = 0; i < MAX_POLLS; i++) {
        if (cancelledRef.current) return;
        await delay(POLL_INTERVAL_MS);
        if (cancelledRef.current) return;

        const job = await api.getGenerationJob(jobId);
        if (job.status === 'Succeeded' && job.workflow) {
          onGenerated(job.workflow, job.openSlots);
          return;
        }
        if (job.status === 'Failed') {
          const lines = job.error ? [job.error] : job.diagnostics;
          setErrorLines(lines.length > 0 ? lines : ['Generation failed for an unknown reason.']);
          setPhase('error');
          return;
        }
      }

      setErrorLines(['Generation timed out. Try a simpler description or check that the AI key is configured.']);
      setPhase('error');
    } catch (err) {
      if (cancelledRef.current) return;
      setErrorLines([err instanceof Error ? err.message : 'Generation request failed.']);
      setPhase('error');
    }
  }

  const busy = phase === 'generating';

  return (
    <div
      style={{ position: 'fixed', inset: 0, background: 'rgba(4,7,13,.85)', backdropFilter: 'blur(4px)', display: 'grid', placeItems: 'center', zIndex: 1000 }}
      onMouseDown={onScrimMouseDown}
    >
      <div
        style={{ background: '#0d1422', border: '1px solid #1e2a3a', borderRadius: 18, width: 560, maxWidth: '95vw', boxShadow: '0 20px 50px rgba(0,0,0,.6)' }}
        onClick={(e) => e.stopPropagation()}
      >
        <div style={{ padding: '20px 24px 16px', borderBottom: '1px solid #1a2433' }}>
          <div style={{ fontSize: 17, fontWeight: 700, color: '#fff', marginBottom: 4 }}>
            {refine ? 'Refine workflow with AI' : 'Generate workflow with AI'}
          </div>
          <div style={{ fontSize: 12.5, color: '#7a8899' }}>
            {refine
              ? 'Describe the change to make. The updated flow opens on the canvas for review before you save.'
              : 'Describe what the workflow should do. The generated flow opens on the canvas for review before you save.'}
          </div>
        </div>

        <div style={{ padding: '20px 24px' }}>
          <textarea
            value={intent}
            onChange={(e) => setIntent(e.target.value)}
            disabled={busy}
            rows={5}
            placeholder={refine
              ? "e.g. Add a Log node after the HTTP request, and change the trigger to a webhook."
              : "e.g. Every morning at 8am, fetch today's weather for Berlin and post it to our Slack channel."}
            aria-label={refine ? 'Workflow change description' : 'Workflow description'}
            style={{
              width: '100%', boxSizing: 'border-box', resize: 'vertical', padding: '12px 14px',
              borderRadius: 10, border: '1px solid #243245', background: '#0a111d', color: '#e6edf5',
              fontSize: 13.5, lineHeight: 1.5, fontFamily: 'inherit',
            }}
          />

          {busy && (
            <div style={{ marginTop: 14, fontSize: 13, color: '#7a8899' }}>
              {refine ? 'Refining' : 'Generating'} and validating… this can take up to a minute.
            </div>
          )}

          {phase === 'error' && (
            <div style={{ marginTop: 14, padding: '12px 14px', borderRadius: 10, border: '1px solid #4a2530', background: 'rgba(120,30,45,.18)' }}>
              <div style={{ fontSize: 12.5, fontWeight: 600, color: '#ffb4b4', marginBottom: 6 }}>Generation failed</div>
              <ul style={{ margin: 0, paddingLeft: 18, fontSize: 12, color: '#d7a6ad' }}>
                {errorLines.map((line, i) => <li key={i} style={{ fontFamily: 'monospace' }}>{line}</li>)}
              </ul>
            </div>
          )}
        </div>

        <div style={{ padding: '0 24px 20px', display: 'flex', gap: 10, justifyContent: 'flex-end' }}>
          <button
            onClick={onClose}
            disabled={busy}
            style={{ padding: '9px 18px', borderRadius: 10, fontSize: 13, fontWeight: 600, cursor: busy ? 'default' : 'pointer', border: '1px solid #243245', background: 'transparent', color: '#8995a6', opacity: busy ? 0.5 : 1 }}
          >
            Cancel
          </button>
          <button
            onClick={generate}
            disabled={busy || intent.trim().length === 0}
            style={{ padding: '9px 20px', borderRadius: 10, fontSize: 13, fontWeight: 700, cursor: busy || intent.trim().length === 0 ? 'default' : 'pointer', border: 'none', background: busy || intent.trim().length === 0 ? '#1d3a52' : '#2f81f7', color: '#fff', opacity: busy || intent.trim().length === 0 ? 0.6 : 1 }}
          >
            {busy ? (refine ? 'Refining…' : 'Generating…') : phase === 'error' ? 'Try again' : refine ? 'Refine' : 'Generate'}
          </button>
        </div>
      </div>
    </div>
  );
}
