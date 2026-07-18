// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { OpenApiImporter } from './OpenApiImporter';
import { useScrimClose } from '../hooks/useScrimClose';

interface CanvasImportModalProps {
  open: boolean;
  onClose: () => void;
  /** Called after a successful import so the caller can refresh available nodes. */
  onImported: () => void;
}

export function CanvasImportModal({ open, onClose, onImported }: CanvasImportModalProps) {
  const onScrimMouseDown = useScrimClose(onClose);
  if (!open) return null;
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
          <div style={{ fontSize: 17, fontWeight: 700, color: '#fff', marginBottom: 4 }}>Import OpenAPI Spec</div>
          <div style={{ fontSize: 12.5, color: '#7a8899' }}>
            Supports OpenAPI 3.0 / 3.1 and Swagger 2.0 in JSON or YAML. The new node will appear in the palette immediately.
          </div>
        </div>
        <div style={{ padding: '20px 24px' }}>
          <OpenApiImporter
            onImported={() => {
              onClose();
              onImported();
            }}
          />
        </div>
        <div style={{ padding: '0 24px 20px', display: 'flex', justifyContent: 'flex-start' }}>
          <button
            onClick={onClose}
            style={{ padding: '9px 18px', borderRadius: 10, fontSize: 13, fontWeight: 600, cursor: 'pointer', border: '1px solid #243245', background: 'transparent', color: '#8995a6' }}
          >
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
