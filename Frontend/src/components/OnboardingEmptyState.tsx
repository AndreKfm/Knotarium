import { useEffect, useState } from 'react';
import { Plus, Sparkles, RefreshCw, AlertTriangle } from 'lucide-react';
import { api } from '../utils/api';
import type { GalleryTemplate } from '../types';

interface OnboardingEmptyStateProps {
  /** Open a fresh, blank workflow in the editor. */
  onCreateBlank: () => void;
  /** Open a workflow (by id) in the editor — used after installing a sample. */
  onOpenWorkflow: (workflowId: string) => void;
}

/**
 * First-run welcome shown when the instance has no workflows (and none archived). Offers two paths:
 * build from scratch, or install a built-in starter and jump straight into it. Replaces the empty
 * two-panel dashboard so a fresh install has an obvious next step.
 */
export function OnboardingEmptyState({ onCreateBlank, onOpenWorkflow }: OnboardingEmptyStateProps) {
  const [templates, setTemplates] = useState<GalleryTemplate[] | null>(null);
  const [installing, setInstalling] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.listGalleryTemplates()
      .then((t) => { if (!cancelled) setTemplates(t); })
      .catch(() => { if (!cancelled) setTemplates([]); });
    return () => { cancelled = true; };
  }, []);

  const install = async (templateId: string) => {
    setInstalling(templateId);
    setError(null);
    try {
      const res = await api.installGalleryTemplate(templateId);
      onOpenWorkflow(res.workflowId);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not install the sample.');
      setInstalling(null);
    }
  };

  return (
    <div
      style={{
        display: 'flex',
        flex: 1,
        alignItems: 'flex-start',
        justifyContent: 'center',
        minHeight: '300px',
        paddingTop: '4vh',
      }}
    >
      <div style={{ width: 'min(760px, 100%)', textAlign: 'center' }}>
        <div
          style={{
            width: 56,
            height: 56,
            margin: '0 auto 20px',
            borderRadius: 16,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            background: 'color-mix(in srgb, var(--color-accent) 16%, transparent)',
            color: 'var(--color-accent)',
          }}
        >
          <Sparkles size={26} />
        </div>

        <h2 style={{ fontSize: '1.5rem', fontWeight: 800, letterSpacing: '-0.02em', color: 'var(--text-primary)' }}>
          Welcome — let’s build your first workflow
        </h2>
        <p style={{ color: 'var(--text-secondary)', marginTop: '8px', fontSize: '0.98rem', lineHeight: 1.5 }}>
          This instance is empty. Start from a blank canvas, or install a ready-to-run sample and open it right away.
        </p>

        <button
          onClick={onCreateBlank}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '8px',
            margin: '24px auto 0',
            padding: '13px 22px',
            borderRadius: '10px',
            background: 'var(--color-accent)',
            border: 'none',
            color: '#fff',
            fontWeight: 700,
            fontSize: '0.95rem',
            cursor: 'pointer',
            boxShadow: '0 4px 14px var(--color-accent-glow)',
          }}
        >
          <Plus size={18} />
          Create your first workflow
        </button>

        {error && (
          <div style={{ marginTop: '20px', padding: '12px 16px', background: 'rgba(239, 68, 68, 0.1)', border: '1px solid var(--color-error)', borderRadius: '10px', color: 'var(--color-error)', display: 'flex', alignItems: 'center', gap: '10px', justifyContent: 'center' }}>
            <AlertTriangle size={16} />
            <span>{error}</span>
          </div>
        )}

        {templates === null ? (
          <div style={{ marginTop: '36px', color: 'var(--text-secondary)', display: 'flex', alignItems: 'center', gap: '10px', justifyContent: 'center' }}>
            <RefreshCw size={16} style={{ animation: 'spin 2s linear infinite' }} />
            <span style={{ fontSize: '0.9rem' }}>Loading samples…</span>
          </div>
        ) : templates.length > 0 ? (
          <div style={{ marginTop: '40px', textAlign: 'left' }}>
            <div style={{ fontSize: '0.78rem', fontWeight: 700, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--text-secondary)', marginBottom: '14px', textAlign: 'center' }}>
              Or start from a sample
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: '14px' }}>
              {templates.map((t) => (
                <div
                  key={t.templateId}
                  style={{
                    padding: '16px',
                    borderRadius: '12px',
                    background: 'rgba(255, 255, 255, 0.02)',
                    border: '1px solid var(--border-color)',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: '8px',
                  }}
                >
                  <div style={{ fontWeight: 700, color: 'var(--text-primary)', fontSize: '0.95rem' }}>{t.manifest.name}</div>
                  <div style={{ color: 'var(--text-secondary)', fontSize: '0.83rem', lineHeight: 1.45, flex: 1 }}>{t.manifest.description}</div>
                  <button
                    onClick={() => void install(t.templateId)}
                    disabled={installing !== null}
                    style={{
                      marginTop: '4px',
                      padding: '8px 12px',
                      borderRadius: '8px',
                      background: 'rgba(255, 255, 255, 0.04)',
                      border: '1px solid var(--border-color)',
                      color: 'var(--text-primary)',
                      fontSize: '0.85rem',
                      fontWeight: 600,
                      cursor: installing !== null ? 'default' : 'pointer',
                      opacity: installing !== null && installing !== t.templateId ? 0.5 : 1,
                      display: 'inline-flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      gap: '7px',
                    }}
                  >
                    {installing === t.templateId ? (
                      <>
                        <RefreshCw size={14} style={{ animation: 'spin 2s linear infinite' }} />
                        Installing…
                      </>
                    ) : (
                      'Install & open'
                    )}
                  </button>
                </div>
              ))}
            </div>
          </div>
        ) : null}
      </div>
    </div>
  );
}
