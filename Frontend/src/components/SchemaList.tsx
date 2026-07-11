import { useState } from 'react';
import type { ApiSchema } from '../types';

interface SchemaListProps {
  schemas: ApiSchema[];
}

export function SchemaList({ schemas }: SchemaListProps) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  if (schemas.length === 0) {
    return (
      <div style={{ padding: '32px 0', textAlign: 'center', color: '#566173', fontSize: 13 }}>
        No schemas defined in this spec.
      </div>
    );
  }

  const toggle = (name: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      next.has(name) ? next.delete(name) : next.add(name);
      return next;
    });

  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))', gap: 12 }}>
      {schemas.map((schema) => {
        const isOpen = expanded.has(schema.name);
        return (
          <div
            key={schema.name}
            style={{ padding: 16, border: '1px solid #1a2433', borderRadius: 12, background: '#0d1422' }}
          >
            <div
              style={{
                fontFamily: 'ui-monospace, Menlo, monospace',
                fontSize: 14,
                fontWeight: 700,
                color: '#c4c2fc',
                marginBottom: schema.description ? 6 : 10,
              }}
            >
              {schema.name}
            </div>
            {schema.description && (
              <div style={{ fontSize: 12, color: '#7a8899', lineHeight: 1.5, marginBottom: 10 }}>
                {schema.description}
              </div>
            )}
            <button
              onClick={() => toggle(schema.name)}
              style={{
                fontSize: 11,
                fontWeight: 600,
                color: '#566173',
                background: '#0a101c',
                border: '1px solid #1a2433',
                borderRadius: 6,
                padding: '4px 10px',
                cursor: 'pointer',
              }}
            >
              {isOpen ? 'Hide JSON ▲' : 'View JSON ▼'}
            </button>
            {isOpen && (
              <pre
                style={{
                  marginTop: 10,
                  padding: 12,
                  background: '#060b14',
                  border: '1px solid #1a2433',
                  borderRadius: 8,
                  fontSize: 11,
                  color: '#9ab',
                  overflowX: 'auto',
                  whiteSpace: 'pre-wrap',
                  wordBreak: 'break-all',
                  maxHeight: 300,
                  overflowY: 'auto',
                }}
              >
                {(() => {
                  try {
                    return JSON.stringify(JSON.parse(schema.schemaJson), null, 2);
                  } catch {
                    return schema.schemaJson;
                  }
                })()}
              </pre>
            )}
          </div>
        );
      })}
    </div>
  );
}
