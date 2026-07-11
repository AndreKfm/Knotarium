import { useEffect, useState } from 'react';
import type { ImportedSpec } from '../types';
import { listSpecs, deleteSpec } from '../utils/openApiClient';
import { OpenApiImporter } from './OpenApiImporter';
import { OperationBrowser } from './OperationBrowser';
import { ServerConfigManager } from './ServerConfigManager';

export function OpenApiView() {
  const [subView, setSubView] = useState<'specs' | 'configs'>('specs');
  const [prefilledBaseUrl, setPrefilledBaseUrl] = useState<string | null>(null);

  const [specs, setSpecs] = useState<ImportedSpec[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [loadingList, setLoadingList] = useState(true);
  const [listError, setListError] = useState<string | null>(null);
  const [showImportModal, setShowImportModal] = useState(false);
  const [specToDelete, setSpecToDelete] = useState<ImportedSpec | null>(null);

  const loadSpecs = () => {
    setLoadingList(true);
    setListError(null);
    listSpecs()
      .then((data) => {
        setSpecs(data);
        if (data.length > 0 && !selectedId) {
          setSelectedId(data[0].id);
        }
      })
      .catch((err) => setListError(err instanceof Error ? err.message : 'Failed to load specs.'))
      .finally(() => setLoadingList(false));
  };

  useEffect(() => {
    loadSpecs();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleImported = (spec: ImportedSpec) => {
    setShowImportModal(false);
    setSpecs((prev) => {
      const exists = prev.some((s) => s.id === spec.id);
      return exists
        ? prev.map((s) => (s.id === spec.id ? spec : s))
        : [spec, ...prev];
    });
    setSelectedId(spec.id);
    loadSpecs();
  };

  const handleDeleteSpec = async (spec: ImportedSpec) => {
    try {
      await deleteSpec(spec.id);
      setSpecs((prev) => prev.filter((s) => s.id !== spec.id));
      if (selectedId === spec.id) {
        setSelectedId(null);
      }
    } catch (err) {
      alert(`Failed to delete spec: ${err instanceof Error ? err.message : 'Unknown error'}`);
    }
  };

  const handleUseSpecServer = (baseUrl: string) => {
    setPrefilledBaseUrl(baseUrl);
    setSubView('configs');
  };

  const formatDate = (iso: string) => {
    try {
      return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
    } catch {
      return iso;
    }
  };

  return (
    <div style={{ display: 'flex', height: '100%', color: '#e6edf3', fontFamily: 'inherit', overflow: 'hidden' }}>
      <style>{`.oav-spec-row:hover .oav-del-btn { opacity: 1 !important; } .oav-del-btn:hover { color: #f0556d !important; background: rgba(240,85,109,.12) !important; }`}</style>
      {/* ── Left sidebar: spec list or context ── */}
      <div style={{ width: 280, flexShrink: 0, display: 'flex', flexDirection: 'column', borderRight: '1px solid #1a2433', background: '#080d16', overflow: 'hidden' }}>
        
        {/* Sub-routing sidebar tabs */}
        <div style={{ display: 'flex', borderBottom: '1px solid #1a2433', flexShrink: 0 }}>
          <button
            onClick={() => setSubView('specs')}
            style={{
              flex: 1,
              padding: '14px',
              border: 'none',
              background: subView === 'specs' ? 'rgba(255, 255, 255, 0.05)' : 'transparent',
              borderBottom: `2px solid ${subView === 'specs' ? 'var(--color-accent, #6366f1)' : 'transparent'}`,
              color: subView === 'specs' ? '#fff' : 'var(--text-secondary, #94a3b8)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
            }}
          >
            APIs
          </button>
          <button
            onClick={() => setSubView('configs')}
            style={{
              flex: 1,
              padding: '14px',
              border: 'none',
              background: subView === 'configs' ? 'rgba(255, 255, 255, 0.05)' : 'transparent',
              borderBottom: `2px solid ${subView === 'configs' ? 'var(--color-accent, #6366f1)' : 'transparent'}`,
              color: subView === 'configs' ? '#fff' : 'var(--text-secondary, #94a3b8)',
              cursor: 'pointer',
              fontWeight: 600,
              fontSize: '0.85rem',
            }}
          >
            Server Configs
          </button>
        </div>

        {subView === 'specs' ? (
          <>
            <div style={{ padding: '16px 16px 12px', borderBottom: '1px solid #1a2433' }}>
              <div style={{ fontSize: 11, letterSpacing: '.1em', fontWeight: 700, color: '#566173', textTransform: 'uppercase', marginBottom: 10 }}>
                Imported APIs
              </div>
              <button
                onClick={() => setShowImportModal(true)}
                style={{
                  width: '100%',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  gap: 8,
                  padding: '10px 14px',
                  borderRadius: 10,
                  background: 'rgba(111,108,240,.15)',
                  border: '1px solid rgba(111,108,240,.35)',
                  color: '#9d9af8',
                  fontSize: 13,
                  fontWeight: 600,
                  cursor: 'pointer',
                }}
              >
                + Import Spec
              </button>
            </div>

            <div style={{ flex: 1, overflowY: 'auto', padding: 8 }}>
              {loadingList && (
                <div style={{ padding: '24px 12px', textAlign: 'center', color: '#566173', fontSize: 12 }}>Loading…</div>
              )}
              {!loadingList && listError && (
                <div style={{ padding: '12px', fontSize: 12, color: '#f0556d' }}>{listError}</div>
              )}
              {!loadingList && !listError && specs.length === 0 && (
                <div style={{ padding: '32px 12px', textAlign: 'center', color: '#566173', fontSize: 13, lineHeight: 1.6 }}>
                  No specs imported yet.<br />Click "Import Spec" to get started.
                </div>
              )}
              {specs.map((spec) => (
                <div
                  key={spec.id}
                  onClick={() => setSelectedId(spec.id)}
                  style={{
                    position: 'relative',
                    padding: '12px 14px',
                    borderRadius: 12,
                    cursor: 'pointer',
                    border: `1px solid ${selectedId === spec.id ? 'rgba(111,108,240,.4)' : 'transparent'}`,
                    background: selectedId === spec.id ? '#111733' : 'transparent',
                    marginBottom: 4,
                    transition: 'all .15s',
                  }}
                  className="oav-spec-row"
                >
                  <div style={{ fontSize: 13.5, fontWeight: 600, color: '#e6edf3', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', marginBottom: 5, paddingRight: 28 }}>
                    {spec.title || spec.id}
                  </div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                    <span style={{ fontSize: 10, fontWeight: 700, padding: '2px 7px', borderRadius: 5, background: '#121a28', border: '1px solid #1e2a3a', color: '#7a8899', textTransform: 'uppercase', letterSpacing: '.05em' }}>
                      {spec.apiVersion || 'API'}
                    </span>
                    <span style={{ fontSize: 11, color: '#566173' }}>v{spec.latestVersionNumber}</span>
                    {spec.importedAtUtc && (
                      <span style={{ fontSize: 11, color: '#566173', marginLeft: 'auto' }}>{formatDate(spec.importedAtUtc)}</span>
                    )}
                  </div>
                  <button
                    title="Delete spec"
                    onClick={(e) => { e.stopPropagation(); setSpecToDelete(spec); }}
                    style={{
                      position: 'absolute',
                      top: 10,
                      right: 10,
                      width: 24,
                      height: 24,
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      background: 'transparent',
                      border: 'none',
                      borderRadius: 6,
                      cursor: 'pointer',
                      color: '#566173',
                      opacity: 0,
                      transition: 'opacity .15s, color .15s',
                    }}
                    className="oav-del-btn"
                  >
                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                      <path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2m2 0v14a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V6" />
                    </svg>
                  </button>
                </div>
              ))}
            </div>
          </>
        ) : (
          <div style={{ flex: 1, padding: '24px 16px', color: '#566173', fontSize: 13, lineHeight: 1.6 }}>
            Server Configurations management is active.<br /><br />Use the main panel to view, create, edit, or delete configurations.
          </div>
        )}
      </div>

      {/* ── Main area ── */}
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        {subView === 'configs' ? (
          <ServerConfigManager
            prefilledBaseUrl={prefilledBaseUrl}
            onClearPrefilledBaseUrl={() => setPrefilledBaseUrl(null)}
          />
        ) : selectedId ? (
          <>
            <div style={{ padding: '18px 24px 0', flexShrink: 0 }}>
              {(() => {
                const spec = specs.find((s) => s.id === selectedId);
                return spec ? (
                  <>
                    <div style={{ fontSize: 20, fontWeight: 700, color: '#fff', marginBottom: 6 }}>{spec.title}</div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16 }}>
                      <span style={{ fontSize: 11, fontWeight: 700, padding: '3px 9px', borderRadius: 6, background: '#121a28', border: '1px solid #1e2a3a', color: '#7a8899', textTransform: 'uppercase', letterSpacing: '.06em' }}>
                        {spec.apiVersion || 'Unknown format'}
                      </span>
                      <span style={{ fontSize: 12, color: '#566173' }}>Version {spec.latestVersionNumber}</span>
                    </div>
                  </>
                ) : null;
              })()}
            </div>
            <div style={{ flex: 1, overflow: 'hidden' }}>
              <OperationBrowser
                key={selectedId}
                specId={selectedId}
                onUseSpecServer={handleUseSpecServer}
              />
            </div>
          </>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '100%', textAlign: 'center', color: '#566173' }}>
            <div style={{ fontSize: 48, marginBottom: 16, opacity: .3 }}>⌗</div>
            <div style={{ fontSize: 16, fontWeight: 600, color: '#9aa6b5', marginBottom: 8 }}>No spec selected</div>
            <div style={{ fontSize: 13, lineHeight: 1.6 }}>
              Import an OpenAPI or Swagger spec, then select it from the left panel to browse its operations and schemas.
            </div>
          </div>
        )}
      </div>

      {/* ── Delete confirmation modal ── */}
      {specToDelete && (
        <div
          style={{ position: 'fixed', inset: 0, background: 'rgba(4,7,13,.85)', backdropFilter: 'blur(4px)', display: 'grid', placeItems: 'center', zIndex: 1000 }}
          onClick={() => setSpecToDelete(null)}
        >
          <div
            style={{ background: '#0d1422', border: '1px solid #1e2a3a', borderRadius: 18, padding: 24, width: '90%', maxWidth: 420, boxShadow: '0 20px 50px rgba(0,0,0,.6)', color: '#e6edf3' }}
            onClick={(e) => e.stopPropagation()}
          >
            <div style={{ fontSize: 18, fontWeight: 700, color: '#fff', marginBottom: 12 }}>Delete "{specToDelete.title || specToDelete.id}"?</div>
            <div style={{ fontSize: 14.5, color: '#9aa6b5', lineHeight: 1.5, marginBottom: 24 }}>
              This will permanently delete the API spec and all its imported versions. This action cannot be undone.
            </div>
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 12 }}>
              <button
                onClick={() => setSpecToDelete(null)}
                style={{ padding: '10px 18px', borderRadius: 10, fontSize: 13.5, fontWeight: 600, cursor: 'pointer', border: '1px solid #243245', background: 'transparent', color: '#8995a6' }}
              >
                Cancel
              </button>
              <button
                onClick={() => { handleDeleteSpec(specToDelete); setSpecToDelete(null); }}
                style={{ padding: '10px 18px', borderRadius: 10, fontSize: 13.5, fontWeight: 600, cursor: 'pointer', border: 'none', background: '#f0556d', color: '#fff' }}
              >
                Delete Spec
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ── Import modal ── */}
      {showImportModal && (
        <div
          style={{ position: 'fixed', inset: 0, background: 'rgba(4,7,13,.85)', backdropFilter: 'blur(4px)', display: 'grid', placeItems: 'center', zIndex: 1000 }}
          onClick={() => setShowImportModal(false)}
        >
          <div
            style={{ background: '#0d1422', border: '1px solid #1e2a3a', borderRadius: 18, width: 560, maxWidth: '95vw', boxShadow: '0 20px 50px rgba(0,0,0,.6)' }}
            onClick={(e) => e.stopPropagation()}
          >
            <div style={{ padding: '20px 24px 16px', borderBottom: '1px solid #1a2433' }}>
              <div style={{ fontSize: 17, fontWeight: 700, color: '#fff', marginBottom: 4 }}>Import OpenAPI Spec</div>
              <div style={{ fontSize: 12.5, color: '#7a8899' }}>
                Supports OpenAPI 3.0 / 3.1 and Swagger 2.0 in JSON or YAML.
              </div>
            </div>
            <div style={{ padding: '20px 24px' }}>
              <OpenApiImporter onImported={handleImported} />
            </div>
            <div style={{ padding: '0 24px 20px', display: 'flex', justifyContent: 'flex-start' }}>
              <button
                onClick={() => setShowImportModal(false)}
                style={{ padding: '9px 18px', borderRadius: 10, fontSize: 13, fontWeight: 600, cursor: 'pointer', border: '1px solid #243245', background: 'transparent', color: '#8995a6' }}
              >
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
