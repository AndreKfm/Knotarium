import type { NodeState } from '../../types';

/** Parse a `variablesBefore` JSON snapshot into a plain object (null when absent/invalid). */
export function parseVariables(raw?: string): Record<string, unknown> | null {
  if (!raw) {
    return null;
  }
  try {
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** A single labelled key/value panel (Inputs, Outputs, Variables). Shared by the run-view time-travel
 *  inspector and the editor-side per-node inspector so both render node I/O identically. */
export function StatePanel({ title, entries, emptyLabel }: { title: string; entries: Array<[string, unknown]>; emptyLabel: string }) {
  return (
    <div style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ fontSize: '0.68rem', textTransform: 'uppercase', letterSpacing: '0.08em', color: '#7dd3fc', fontWeight: 700 }}>
        {title}
      </div>
      <div
        style={{
          background: '#030712',
          border: '1px solid rgba(148, 163, 184, 0.12)',
          borderRadius: 10,
          padding: 10,
          flex: 1,
          minHeight: 0,
          overflow: 'auto',
          display: 'flex',
          flexDirection: 'column',
          gap: 6,
        }}
      >
        {entries.length === 0 ? (
          <span style={{ color: '#64748b', fontStyle: 'italic', fontSize: '0.76rem' }}>{emptyLabel}</span>
        ) : (
          entries.map(([key, value]) => (
            <div key={key} style={{ display: 'flex', gap: 8, fontFamily: 'monospace', fontSize: '0.76rem', minWidth: 0 }}>
              <span style={{ color: '#67e8f9', flex: '0 0 auto' }}>{key}</span>
              <span style={{ color: '#e2e8f0', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {typeof value === 'string' ? value : JSON.stringify(value)}
              </span>
            </div>
          ))
        )}
      </div>
    </div>
  );
}

/** The Inputs / Variables / Outputs trio for one node's recorded state.
 *  `layout='row'` for the wide run view, `layout='column'` for the narrow properties panel. */
export function NodeIoPanels({ nodeState, layout = 'row' }: { nodeState?: NodeState | null; layout?: 'row' | 'column' }) {
  const variablesBefore = parseVariables(nodeState?.variablesBefore);
  return (
    <div style={{ display: 'flex', flexDirection: layout === 'row' ? 'row' : 'column', gap: 14, flex: 1, minHeight: 0 }}>
      <StatePanel title="Inputs" entries={Object.entries(nodeState?.inputs ?? {})} emptyLabel="No inputs" />
      <StatePanel
        title="Variables at this step"
        entries={variablesBefore ? Object.entries(variablesBefore) : []}
        emptyLabel={nodeState?.variablesBefore === undefined ? 'Not captured' : 'No variables set'}
      />
      <StatePanel title="Outputs" entries={Object.entries(nodeState?.outputs ?? {})} emptyLabel="No outputs" />
    </div>
  );
}
