import { useEffect, useState } from 'react';
import type { SpecDetail } from '../types';
import { getSpecDetail } from '../utils/openApiClient';
import { SchemaList } from './SchemaList';

interface OperationBrowserProps {
  specId: string;
  onUseSpecServer?: (baseUrl: string) => void;
}

type BrowserTab = 'operations' | 'schemas';

const METHOD_COLORS: Record<string, { bg: string; color: string; border: string }> = {
  GET:    { bg: 'rgba(52,211,153,.15)',  color: '#34d399', border: 'rgba(52,211,153,.3)' },
  POST:   { bg: 'rgba(96,165,250,.15)', color: '#60a5fa', border: 'rgba(96,165,250,.3)' },
  PUT:    { bg: 'rgba(240,180,41,.15)', color: '#f0b429', border: 'rgba(240,180,41,.3)' },
  DELETE: { bg: 'rgba(240,85,109,.15)', color: '#f0556d', border: 'rgba(240,85,109,.3)' },
  PATCH:  { bg: 'rgba(167,139,250,.15)', color: '#a78bfa', border: 'rgba(167,139,250,.3)' },
  HEAD:   { bg: 'rgba(156,163,175,.15)', color: '#9ca3af', border: 'rgba(156,163,175,.3)' },
};

function methodStyle(method: string): React.CSSProperties {
  const m = (METHOD_COLORS[method.toUpperCase()] ?? {
    bg: 'rgba(156,163,175,.1)', color: '#9ca3af', border: 'rgba(156,163,175,.2)',
  });
  return {
    fontSize: 10.5,
    fontWeight: 800,
    padding: '3px 8px',
    borderRadius: 5,
    letterSpacing: '.07em',
    minWidth: 54,
    textAlign: 'center',
    flexShrink: 0,
    background: m.bg,
    color: m.color,
    border: `1px solid ${m.border}`,
  };
}

export function OperationBrowser({ specId, onUseSpecServer }: OperationBrowserProps) {
  const [detail, setDetail] = useState<SpecDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const [activeTab, setActiveTab] = useState<BrowserTab>('operations');

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setDetail(null);

    getSpecDetail(specId)
      .then((d) => {
        if (cancelled) return;
        setDetail(d);
        setExpandedGroups(new Set(d.groups.map((g) => g.tag)));
      })
      .catch((err) => {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : 'Failed to load spec.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => { cancelled = true; };
  }, [specId]);

  const toggleGroup = (tag: string) =>
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      next.has(tag) ? next.delete(tag) : next.add(tag);
      return next;
    });

  if (loading) {
    return (
      <div style={{ padding: '40px 20px', textAlign: 'center', color: '#566173', fontSize: 13 }}>
        Loading operations…
      </div>
    );
  }

  if (error) {
    return (
      <div role="alert" style={{ margin: 20, padding: '14px 18px', border: '1px solid rgba(240,85,109,.3)', borderRadius: 10, background: 'rgba(240,85,109,.08)', color: '#f0556d', fontSize: 13 }}>
        {error}
      </div>
    );
  }

  if (!detail) return null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Tab bar */}
      <div style={{ display: 'flex', borderBottom: '1px solid #1a2433', flexShrink: 0 }}>
        {(['operations', 'schemas'] as BrowserTab[]).map((t) => (
          <button
            key={t}
            onClick={() => setActiveTab(t)}
            style={{
              padding: '12px 20px',
              fontSize: 13,
              fontWeight: 600,
              color: activeTab === t ? '#9d9af8' : '#566173',
              background: 'transparent',
              border: 0,
              borderBottom: `2px solid ${activeTab === t ? '#6f6cf0' : 'transparent'}`,
              cursor: 'pointer',
              marginBottom: -1,
              textTransform: 'capitalize',
            }}
          >
            {t}
            {t === 'operations' && detail.groups.length > 0 && (
              <span style={{ marginLeft: 6, fontSize: 11, background: '#121a28', border: '1px solid #1e2a3a', borderRadius: 999, padding: '1px 7px', color: '#566173', fontWeight: 600 }}>
                {detail.groups.reduce((sum, g) => sum + g.operations.length, 0)}
              </span>
            )}
            {t === 'schemas' && detail.schemas.length > 0 && (
              <span style={{ marginLeft: 6, fontSize: 11, background: '#121a28', border: '1px solid #1e2a3a', borderRadius: 999, padding: '1px 7px', color: '#566173', fontWeight: 600 }}>
                {detail.schemas.length}
              </span>
            )}
          </button>
        ))}
      </div>

      {activeTab === 'operations' && detail.defaultServers && detail.defaultServers.length > 0 && onUseSpecServer && (
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '10px 20px', background: 'rgba(111,108,240,.06)', borderBottom: '1px solid #1a2433', flexShrink: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <span style={{ fontSize: '0.8rem', fontWeight: 600, color: 'var(--text-secondary, #94a3b8)' }}>Spec Server:</span>
            <code style={{ fontSize: '0.8rem', color: '#c4c2fc' }}>{detail.defaultServers[0]}</code>
          </div>
          <button
            onClick={() => onUseSpecServer(detail.defaultServers![0])}
            style={{
              background: 'rgba(111,108,240,.15)',
              border: '1px solid rgba(111,108,240,.35)',
              color: '#9d9af8',
              borderRadius: '6px',
              padding: '4px 10px',
              fontSize: '0.78rem',
              fontWeight: 600,
              cursor: 'pointer',
            }}
          >
            Use this spec's server
          </button>
        </div>
      )}

      {/* Content */}
      <div style={{ flex: 1, overflowY: 'auto', padding: 20 }}>
        {activeTab === 'operations' && (
          detail.groups.length === 0 ? (
            <div style={{ padding: '32px 0', textAlign: 'center', color: '#566173', fontSize: 13 }}>
              No operations found in this spec.
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {detail.groups.map((group) => {
                const isExpanded = expandedGroups.has(group.tag);
                return (
                  <div key={group.tag} style={{ border: '1px solid #1a2433', borderRadius: 12, overflow: 'hidden' }}>
                    {/* Group header */}
                    <div
                      role="button"
                      tabIndex={0}
                      onClick={() => toggleGroup(group.tag)}
                      onKeyDown={(e) => e.key === 'Enter' && toggleGroup(group.tag)}
                      style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '12px 16px', background: '#0d1422', cursor: 'pointer', userSelect: 'none' }}
                    >
                      <span style={{ fontSize: 13, fontWeight: 700, color: '#c4c2fc' }}>{group.tag}</span>
                      <span style={{ fontSize: 11, color: '#566173', background: '#121a28', border: '1px solid #1e2a3a', padding: '2px 8px', borderRadius: 999, fontWeight: 600 }}>
                        {group.operations.length}
                      </span>
                      <span style={{ marginLeft: 'auto', color: '#566173', transform: isExpanded ? 'none' : 'rotate(-90deg)', display: 'inline-block', transition: 'transform .18s ease' }}>▼</span>
                    </div>
                    {/* Operations list */}
                    {isExpanded && (
                      <div style={{ borderTop: '1px solid #1a2433' }}>
                        {group.operations.map((op) => (
                          <div
                            key={op.operationId}
                            draggable={true}
                            onDragStart={(e) => {
                              const safe = specId.toLowerCase().replace(/[^a-z0-9\-]/g, '');
                              const packageId = 'openapi.' + (safe || 'spec');
                              const dragData = {
                                type: 'openapi-operation',
                                specId,
                                packageId,
                                operationId: op.operationId,
                              };
                              e.dataTransfer.setData('application/json', JSON.stringify(dragData));
                              e.dataTransfer.effectAllowed = 'copy';
                            }}
                            style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '10px 16px', borderBottom: '1px solid #101820', cursor: 'grab' }}
                          >
                            <span style={methodStyle(op.method) as React.CSSProperties}>{op.method.toUpperCase()}</span>
                            <span style={{ fontFamily: 'ui-monospace, Menlo, monospace', fontSize: 13, color: '#e6edf3', flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                              {op.pathTemplate}
                            </span>
                            {op.summary && (
                              <span style={{ fontSize: 12, color: '#566173', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: 220 }}>
                                {op.summary}
                              </span>
                            )}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )
        )}

        {activeTab === 'schemas' && <SchemaList schemas={detail.schemas} />}
      </div>
    </div>
  );
}
