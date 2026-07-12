import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { RefreshCw, AlertTriangle } from 'lucide-react';
import { api } from '../utils/api';
import type { GalleryTemplate } from '../types';
import { SAMPLE_META, DEFAULT_SAMPLE_META, CATEGORY_ACCENT, type SampleCategory } from './onboardingSampleMeta';

interface OnboardingEmptyStateProps {
  /** Open a fresh, blank workflow in the editor. */
  onCreateBlank: () => void;
  /** Open a workflow (by id) in the editor — used after installing a sample. */
  onOpenWorkflow: (workflowId: string) => void;
}

// Line-icon paths (viewBox 0 0 24 24) ported from the Welcome redesign so the tiles and node-flow
// chips render exactly as designed, independent of the icon library.
const G: Record<string, string> = {
  manual: '<path d="M6 12.5V6a2 2 0 0 1 4 0v5.5M10 8.5a2 2 0 0 1 4 0V12M14 9.5a2 2 0 0 1 4 0v5a6 6 0 0 1-6 6h-1.2a5 5 0 0 1-3.6-1.6L4 15.6a2 2 0 0 1 3-2.6l1 1"/>',
  delay: '<circle cx="12" cy="13" r="7.5"/><path d="M12 9.5v3.5l2.5 1.8M9 3h6"/>',
  log: '<path d="M5 4h14M5 9h14M5 14h9M5 19h11"/>',
  http: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5c2.6 2.8 2.6 14.2 0 17M12 3.5c-2.6 2.8-2.6 14.2 0 17"/>',
  schedule: '<rect x="3.5" y="5" width="17" height="16" rx="2.5"/><path d="M3.5 9.5h17M8 3v4M16 3v4"/><circle cx="12" cy="15" r="0.4"/>',
  webhook: '<circle cx="12" cy="6" r="2.4"/><circle cx="6" cy="18" r="2.4"/><circle cx="18" cy="18" r="2.4"/><path d="M12 8.4l-3.4 6.6M12 8.4l3.4 6.6M8.4 18h7.2"/>',
  variable: '<path d="M8 4c-2.5 2.5-2.5 13.5 0 16M16 4c2.5 2.5 2.5 13.5 0 16M9.5 9l5 6M14.5 9l-5 6"/>',
  repeat: '<path d="M17 3l3.2 3.2L17 9.4M3.8 11V9.2a4 4 0 0 1 4-4h12.4M7 21l-3.2-3.2L7 14.6M20.2 13v1.8a4 4 0 0 1-4 4H3.8"/>',
  marker: '<path d="M6 3v18M6 4h11l-2.5 4L17 12H6"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  install: '<path d="M12 15V4M8 11l4 4 4-4"/><path d="M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3"/>',
  arrow: '<path d="M5 12h14M13 6l6 6-6 6"/>',
};

function Icon({ k, size = 22, sw = 2, color }: { k: string; size?: number; sw?: number; color?: string }) {
  return (
    <svg
      width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={sw} strokeLinecap="round" strokeLinejoin="round"
      style={color ? { color } : undefined}
      dangerouslySetInnerHTML={{ __html: G[k] ?? '' }}
    />
  );
}

/**
 * First-run welcome shown when the instance has no workflows. Implements the Welcome Page redesign:
 * a hero with a "create blank" CTA, then the built-in gallery starters as category-accented cards with
 * a node-flow preview and one-click install-and-open. Live data (name/description/id) comes from the
 * gallery API; the category/icon/flow presentation comes from onboardingSampleMeta.
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
    <div className="kg-onb">
      <style>{css}</style>

      <div className="hero">
        <div className="brand">
          <div className="hmark">
            <svg
              width={32} height={32} viewBox="0 0 48 48" fill="none" stroke="currentColor"
              strokeWidth={3} strokeLinecap="round" strokeLinejoin="round"
            >
              <path d="M15 8v32" />
              <path d="M15 24l16-14M15 24l16 14" />
              <circle cx="15" cy="8" r="4" fill="currentColor" stroke="none" />
              <circle cx="15" cy="40" r="4" fill="currentColor" stroke="none" />
              <circle cx="33" cy="9" r="4" fill="currentColor" stroke="none" />
              <circle cx="33" cy="39" r="4" fill="currentColor" stroke="none" />
              <circle cx="15" cy="24" r="4.5" fill="currentColor" stroke="none" />
            </svg>
          </div>
          <div className="wordmark"><span className="knot">Knot</span><span className="arium">arium</span></div>
        </div>
        <h1>Welcome — let’s build your first workflow</h1>
        <p>This instance is empty. Start from a blank canvas, or install a ready-to-run sample and open it right away.</p>
        <button className="cta" onClick={onCreateBlank}>
          <Icon k="plus" size={17} sw={2.6} />
          Create your first workflow
        </button>
      </div>

      {error && (
        <div className="onb-err">
          <AlertTriangle size={16} />
          <span>{error}</span>
        </div>
      )}

      <div className="divider"><span className="ln" /><span className="tx">OR START FROM A SAMPLE</span><span className="ln" /></div>

      {templates === null ? (
        <div className="onb-loading">
          <RefreshCw size={16} className="onb-spin" />
          <span>Loading samples…</span>
        </div>
      ) : templates.length > 0 ? (
        <div className="grid">
          {templates.map((t) => {
            const meta = SAMPLE_META[t.templateId] ?? DEFAULT_SAMPLE_META;
            const accent = CATEGORY_ACCENT[meta.category];
            return (
              <div key={t.templateId} className="card" style={{ '--ac': accent } as CSSProperties}>
                <div className="card-top">
                  <span className="tile"><Icon k={meta.icon} /></span>
                  <div style={{ minWidth: 0 }}>
                    <h3>{t.manifest.name}</h3>
                    <div className="cat-tag">{meta.tag}</div>
                  </div>
                </div>
                <p>{t.manifest.description}</p>
                {meta.flow.length > 0 && (
                  <div className="flow">
                    {meta.flow.map((f, i) => (
                      <span key={i} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
                        <span className="chip"><Icon k={f.icon} size={13} color={CATEGORY_ACCENT[f.cat as SampleCategory]} />{f.label}</span>
                        {i < meta.flow.length - 1 && <span className="arrow"><Icon k="arrow" size={12} sw={2.4} /></span>}
                      </span>
                    ))}
                  </div>
                )}
                <button className="install" onClick={() => void install(t.templateId)} disabled={installing !== null}>
                  {installing === t.templateId
                    ? <><RefreshCw size={14} className="onb-spin" /> Installing…</>
                    : <><Icon k="install" size={15} /> Install &amp; open</>}
                </button>
              </div>
            );
          })}
        </div>
      ) : null}
    </div>
  );
}

const css = `
.kg-onb { --line:#1a2230; --line-soft:#141c28; --card:#0d121b; --card-h:#10161f; --muted:#8a95a6; --faint:#5a6675;
  max-width: 1120px; margin: 0 auto; width: 100%; padding: 8px 0 40px; }
.kg-onb .hero { text-align: center; }
.kg-onb .brand { display: inline-flex; align-items: center; gap: 14px; }
.kg-onb .hmark { width: 60px; height: 60px; margin: 0 auto; border-radius: 16px; display: grid; place-items: center;
  background: linear-gradient(155deg, #5d4de0, #7c6cf0); color: #eae7ff; box-shadow: 0 16px 34px -14px rgba(124,108,240,0.7); }
.kg-onb .wordmark { font-size: 30px; font-weight: 800; letter-spacing: -0.03em; }
.kg-onb .wordmark .knot { color: var(--text-primary, #e6edf3); }
.kg-onb .wordmark .arium { color: var(--muted); font-weight: 600; }
.kg-onb h1 { font-size: 30px; font-weight: 800; letter-spacing: -0.025em; margin: 22px 0 0; color: var(--text-primary, #e6edf3); }
.kg-onb .hero p { font-size: 15px; color: var(--muted); margin: 12px auto 0; max-width: 620px; line-height: 1.55; }
.kg-onb .cta { display: inline-flex; align-items: center; gap: 10px; margin-top: 26px; font-size: 15px; font-weight: 700;
  font-family: inherit; border: 0; cursor: pointer; color: #fff; padding: 14px 26px; border-radius: 12px;
  background: linear-gradient(160deg, #8b7cf0, #6355e0); box-shadow: 0 14px 30px -10px rgba(124,108,240,0.75); transition: filter .12s; }
.kg-onb .cta:hover { filter: brightness(1.07); }
.kg-onb .onb-err { max-width: 620px; margin: 20px auto 0; padding: 12px 16px; border-radius: 10px; display: flex; gap: 10px;
  align-items: center; justify-content: center; background: rgba(239,68,68,0.1); border: 1px solid var(--color-error, #ef4444); color: var(--color-error, #ef4444); }
.kg-onb .divider { display: flex; align-items: center; gap: 16px; margin: 42px 0 24px; }
.kg-onb .divider .ln { flex: 1; height: 1px; background: linear-gradient(90deg, transparent, var(--line), transparent); }
.kg-onb .divider .tx { font-size: 11.5px; font-weight: 800; letter-spacing: 0.16em; color: var(--faint); }
.kg-onb .onb-loading { display: flex; align-items: center; gap: 10px; justify-content: center; color: var(--muted); font-size: 14px; }
.kg-onb .onb-spin { animation: onb-spin 1.4s linear infinite; }
@keyframes onb-spin { to { transform: rotate(360deg); } }
.kg-onb .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 18px; }
@media (max-width: 900px) { .kg-onb .grid { grid-template-columns: repeat(2, 1fr); } }
@media (max-width: 620px) { .kg-onb .grid { grid-template-columns: 1fr; } }
.kg-onb .card { position: relative; border: 1px solid var(--line); border-radius: 16px; background: var(--card);
  padding: 18px 18px 16px; display: flex; flex-direction: column; overflow: hidden; transition: border-color .14s, transform .14s, background .14s; }
.kg-onb .card::before { content: ""; position: absolute; left: 0; right: 0; top: 0; height: 2px; background: var(--ac); opacity: 0; transition: opacity .14s; }
.kg-onb .card:hover { border-color: color-mix(in srgb, var(--ac) 40%, var(--line)); transform: translateY(-2px); background: var(--card-h); }
.kg-onb .card:hover::before { opacity: .9; }
.kg-onb .card-top { display: flex; align-items: center; gap: 12px; }
.kg-onb .tile { width: 44px; height: 44px; border-radius: 12px; flex: none; display: grid; place-items: center;
  background: color-mix(in srgb, var(--ac) 13%, transparent); border: 1px solid color-mix(in srgb, var(--ac) 32%, transparent); color: var(--ac); }
.kg-onb .card h3 { margin: 0; font-size: 16.5px; font-weight: 700; letter-spacing: -0.01em; color: var(--text-primary, #e6edf3); overflow: hidden; text-overflow: ellipsis; }
.kg-onb .cat-tag { margin-top: 3px; font-size: 10.5px; font-weight: 700; letter-spacing: 0.08em; color: var(--ac); opacity: .92; }
.kg-onb .card p { margin: 14px 0 0; font-size: 13px; line-height: 1.55; color: var(--muted); flex: 1; }
.kg-onb .flow { display: flex; align-items: center; gap: 6px; margin-top: 14px; flex-wrap: wrap; }
.kg-onb .chip { display: inline-flex; align-items: center; gap: 5px; font-size: 11px; font-weight: 600; color: #b8c2d0;
  background: #0a0f17; border: 1px solid var(--line); border-radius: 7px; padding: 4px 8px 4px 6px; white-space: nowrap; }
.kg-onb .arrow { color: #35404f; flex: none; display: inline-flex; }
.kg-onb .install { margin-top: 16px; display: flex; align-items: center; justify-content: center; gap: 8px; width: 100%;
  font-size: 13.5px; font-weight: 700; font-family: inherit; cursor: pointer; border-radius: 10px; padding: 11px;
  color: #cdd6e2; background: #0a0f17; border: 1px solid var(--line); transition: all .13s; }
.kg-onb .install:disabled { cursor: default; }
.kg-onb .card:hover .install:not(:disabled) { border-color: color-mix(in srgb, var(--ac) 45%, var(--line)); color: #fff; background: color-mix(in srgb, var(--ac) 12%, #0a0f17); }
`;
