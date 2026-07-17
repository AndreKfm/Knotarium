// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useRef, useState } from 'react';
import type { ImportedSpec } from '../types';
import { importSpec, importSpecFromUrl } from '../utils/openApiClient';

interface OpenApiImporterProps {
  onImported: (spec: ImportedSpec) => void;
}

type ImportTab = 'paste' | 'file' | 'url';

const TAB_BTN: React.CSSProperties = {
  padding: '8px 18px',
  fontSize: 13,
  fontWeight: 600,
  cursor: 'pointer',
  border: '1px solid #1a2433',
  background: 'transparent',
  color: '#566173',
};

const TAB_BTN_ACTIVE: React.CSSProperties = {
  ...TAB_BTN,
  background: '#111733',
  borderColor: 'rgba(111,108,240,.4)',
  color: '#9d9af8',
};

export function OpenApiImporter({ onImported }: OpenApiImporterProps) {
  const [tab, setTab] = useState<ImportTab>('paste');
  const [specId, setSpecId] = useState('');
  const [pasteContent, setPasteContent] = useState('');
  const [url, setUrl] = useState('');
  const [allowInsecure, setAllowInsecure] = useState(false);
  const [file, setFile] = useState<File | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const handleSubmit = async () => {
    setError(null);
    if (tab === 'file') {
      if (!file) {
        setError('Please select a file to upload.');
        return;
      }
    } else if (tab === 'url') {
      if (!url.trim()) {
        setError('Please enter a spec URL.');
        return;
      }
    } else {
      if (!pasteContent.trim()) {
        setError('Please enter some content to import.');
        return;
      }
    }
    setLoading(true);
    try {
      const spec =
        tab === 'file' ? await importSpec(file!, specId)
        : tab === 'url' ? await importSpecFromUrl(url.trim(), specId, allowInsecure)
        : await importSpec(pasteContent.trim(), specId);
      setPasteContent('');
      setUrl('');
      setFile(null);
      setSpecId('');
      onImported(spec);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Import failed.');
    } finally {
      setLoading(false);
    }
  };

  const handleFileDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    const dropped = e.dataTransfer.files[0];
    if (dropped) setFile(dropped);
  };

  return (
    <div>
      {/* Tab switcher */}
      <div style={{ display: 'flex', marginBottom: 16 }}>
        <button
          style={{ ...TAB_BTN_ACTIVE, ...(tab !== 'paste' ? TAB_BTN : {}), borderRadius: '9px 0 0 9px' }}
          onClick={() => setTab('paste')}
        >
          Paste Content
        </button>
        <button
          style={{ ...TAB_BTN, ...(tab === 'file' ? TAB_BTN_ACTIVE : {}), borderLeft: 0 }}
          onClick={() => setTab('file')}
        >
          Upload File
        </button>
        <button
          style={{ ...TAB_BTN, ...(tab === 'url' ? TAB_BTN_ACTIVE : {}), borderRadius: '0 9px 9px 0', borderLeft: 0 }}
          onClick={() => setTab('url')}
        >
          From URL
        </button>
      </div>

      {tab === 'paste' ? (
        <textarea
          value={pasteContent}
          onChange={(e) => setPasteContent(e.target.value)}
          placeholder="Paste OpenAPI / Swagger YAML or JSON here…"
          rows={12}
          aria-label="OpenAPI content"
          style={{
            width: '100%',
            background: '#060b14',
            border: '1px solid #1a2433',
            borderRadius: 10,
            color: '#c4d0e0',
            fontFamily: 'ui-monospace, Menlo, monospace',
            fontSize: 12,
            padding: 14,
            resize: 'vertical',
            outline: 'none',
            boxSizing: 'border-box',
          }}
        />
      ) : tab === 'url' ? (
        <div>
          <input
            type="url"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') handleSubmit(); }}
            placeholder="https://example.com/openapi.json"
            aria-label="OpenAPI spec URL"
            style={{
              width: '100%',
              background: '#060b14',
              border: '1px solid #1a2433',
              borderRadius: 10,
              color: '#c4d0e0',
              fontFamily: 'ui-monospace, Menlo, monospace',
              fontSize: 13,
              padding: '12px 14px',
              outline: 'none',
              boxSizing: 'border-box',
            }}
          />
          <div style={{ fontSize: 12, color: '#566173', marginTop: 8 }}>
            The server fetches the spec (so cross-origin URLs work). Private / loopback hosts are
            blocked unless allowed by the egress policy.
          </div>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, fontSize: 12.5, color: '#9aa6b5', cursor: 'pointer' }}>
            <input type="checkbox" checked={allowInsecure} onChange={(e) => setAllowInsecure(e.target.checked)} />
            Allow self-signed / untrusted certificate
          </label>
          {allowInsecure && (
            <div style={{ fontSize: 11.5, color: '#f0b429', marginTop: 6, lineHeight: 1.45 }}>
              ⚠ Skips TLS certificate validation for this fetch. Use only for trusted dev/LAN servers.
            </div>
          )}
        </div>
      ) : (
        <div>
          <div
            role="button"
            tabIndex={0}
            style={{
              border: `2px dashed ${dragOver ? 'rgba(111,108,240,.5)' : '#1e2a3a'}`,
              borderRadius: 12,
              padding: '40px 24px',
              textAlign: 'center',
              cursor: 'pointer',
              background: dragOver ? 'rgba(111,108,240,.05)' : 'transparent',
              transition: 'all .15s',
            }}
            onClick={() => fileInputRef.current?.click()}
            onKeyDown={(e) => e.key === 'Enter' && fileInputRef.current?.click()}
            onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
            onDragLeave={() => setDragOver(false)}
            onDrop={handleFileDrop}
          >
            <div style={{ color: '#566173', marginBottom: 12, fontSize: 32 }}>↑</div>
            <div style={{ fontSize: 14, color: '#9aa6b5', marginBottom: 6 }}>
              <strong style={{ color: '#c4c2fc' }}>Click to upload</strong> or drag &amp; drop
            </div>
            <div style={{ fontSize: 12, color: '#566173' }}>
              JSON or YAML — OpenAPI 3.0 / 3.1 / Swagger 2.0
            </div>
          </div>
          <input
            ref={fileInputRef}
            type="file"
            accept=".json,.yaml,.yml"
            aria-label="Upload OpenAPI file"
            style={{ display: 'none' }}
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
          />
          {file && (
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '12px 16px', border: '1px solid #1a2433', borderRadius: 10, background: '#0a101c', marginTop: 12 }}>
              <span style={{ flex: 1, fontFamily: 'ui-monospace, Menlo, monospace', fontSize: 13, color: '#9d9af8', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {file.name}
              </span>
              <button
                onClick={() => setFile(null)}
                aria-label="Clear file"
                style={{ background: 'transparent', border: 0, color: '#566173', cursor: 'pointer', padding: 4, borderRadius: 6, display: 'inline-flex' }}
              >
                ✕
              </button>
            </div>
          )}
        </div>
      )}

      {/* Optional explicit spec id — defaults to a slug of the spec title when left blank. */}
      <div style={{ marginTop: 16 }}>
        <label htmlFor="openapi-spec-id" style={{ display: 'block', fontSize: 12, color: '#9aa6b5', marginBottom: 6 }}>
          Spec ID <span style={{ color: '#566173' }}>(optional — set this to keep two APIs with the same title distinct)</span>
        </label>
        <input
          id="openapi-spec-id"
          type="text"
          value={specId}
          onChange={(e) => setSpecId(e.target.value)}
          placeholder="auto from title"
          aria-label="Spec ID"
          style={{
            width: '100%',
            background: '#060b14',
            border: '1px solid #1a2433',
            borderRadius: 10,
            color: '#c4d0e0',
            fontFamily: 'ui-monospace, Menlo, monospace',
            fontSize: 12,
            padding: '10px 14px',
            outline: 'none',
            boxSizing: 'border-box',
          }}
        />
      </div>

      {error && (
        <div role="alert" style={{ marginTop: 12, fontSize: 12.5, color: '#f0556d' }}>
          {error}
        </div>
      )}

      <div style={{ marginTop: 20, display: 'flex', justifyContent: 'flex-end' }}>
        <button
          onClick={handleSubmit}
          disabled={loading}
          aria-label="Import spec"
          style={{
            padding: '10px 24px',
            borderRadius: 10,
            fontSize: 13.5,
            fontWeight: 600,
            cursor: loading ? 'not-allowed' : 'pointer',
            border: '1px solid #5856c5',
            background: loading ? 'rgba(111,108,240,.3)' : '#6f6cf0',
            color: loading ? 'rgba(255,255,255,.4)' : '#fff',
          }}
        >
          {loading ? 'Importing…' : 'Import'}
        </button>
      </div>
    </div>
  );
}
