import { useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { getTypeStyles, type VariableType } from './VariableToken';
import type { SubflowVariable, SubflowInterface } from '../utils/subflowInterface';

export type { SubflowVariable, SubflowInterface } from '../utils/subflowInterface';

export interface DroppedGlobal {
  name: string;
  type: VariableType;
}

const VARIABLE_TOKEN_MIME = 'application/knotgarden-variable-token';

interface SubflowLanesProps {
  subflowName: string;
  /** Current bound rows ({ target, value } for inputs; { source, target } for outputs). */
  inputs: Record<string, unknown>[];
  outputs: Record<string, unknown>[];
  /** Known global variables, used to resolve type dots and flag NEW globals on outputs. */
  variables: SubflowVariable[];
  /** The child subflow's declared interface — the parent binds globals to these declared locals. */
  subflowInterface?: SubflowInterface;
  /** Bind a global/value to a declared local. Omitted => read-only (run view). */
  onBindInput?: (localName: string, value: unknown) => void;
  onBindOutput?: (localName: string, globalName: string) => void;
}

function readDroppedGlobal(e: React.DragEvent): DroppedGlobal | null {
  const raw = e.dataTransfer.getData(VARIABLE_TOKEN_MIME);
  if (!raw) return null;
  try {
    const token = JSON.parse(raw) as { variableName?: string; type?: VariableType };
    if (token.variableName) {
      return { name: token.variableName, type: token.type ?? 'string' };
    }
  } catch {
    // ignore malformed payload
  }
  return null;
}

// Pull the referenced global name out of an input's `value`: a drag-drop variable ref, or a
// {{ $variables.x }} expression. Returns null for literals / free expressions.
function referencedGlobal(value: unknown): string | null {
  if (value && typeof value === 'object') {
    const ref = value as { __type?: unknown; variableName?: unknown };
    if (ref.__type === 'variable_ref' && typeof ref.variableName === 'string') {
      return ref.variableName;
    }
  }
  if (typeof value === 'string') {
    const match = value.match(/\{\{\s*\$variables\.([A-Za-z0-9_$]+)\s*\}\}/);
    if (match) return match[1];
  }
  return null;
}

function isDraggedRef(value: unknown): boolean {
  return !!value && typeof value === 'object' && (value as { __type?: unknown }).__type === 'variable_ref';
}

function displayValue(value: unknown): string {
  const ref = referencedGlobal(value);
  if (ref) return ref;
  if (value === undefined || value === null || value === '') return '—';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}

// Per-slot drop handling shared by interface input/output slots.
function useSlotDrop(onDrop?: (g: DroppedGlobal) => void) {
  const [dragOver, setDragOver] = useState(false);
  const handlers = onDrop
    ? {
        onDragOver: (e: React.DragEvent) => {
          if (!e.dataTransfer.types.includes(VARIABLE_TOKEN_MIME)) return;
          e.preventDefault();
          e.stopPropagation();
          e.dataTransfer.dropEffect = 'copy';
          setDragOver(true);
        },
        onDragLeave: () => setDragOver(false),
        onDrop: (e: React.DragEvent) => {
          setDragOver(false);
          const g = readDroppedGlobal(e);
          if (!g) return;
          e.preventDefault();
          e.stopPropagation();
          onDrop(g);
        },
      }
    : {};
  return { dragOver, handlers };
}

// Inline-editable identifier field rendered directly on the node. Stops pointer propagation so
// clicking into it doesn't start a node drag/selection.
function InlineInput({ value, placeholder, onChange }: { value: string; placeholder: string; onChange: (v: string) => void }) {
  const [focused, setFocused] = useState(false);
  return (
    <input
      className="nodrag"
      value={value}
      placeholder={placeholder}
      spellCheck={false}
      onChange={(e) => onChange(e.target.value)}
      onPointerDown={(e) => e.stopPropagation()}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={{
        flex: 1, minWidth: 0, fontFamily: 'monospace', fontSize: '0.72rem', color: '#e6edf6',
        background: focused ? 'rgba(255,255,255,0.05)' : 'transparent',
        border: `1px solid ${focused ? 'var(--color-accent)' : 'rgba(255,255,255,0.1)'}`,
        borderRadius: 4, padding: '1px 4px', outline: 'none',
      }}
    />
  );
}

function TypeDot({ type }: { type?: VariableType }) {
  const color = type ? getTypeStyles(type).color : 'var(--text-muted)';
  return <span style={{ flex: '0 0 auto', width: 7, height: 7, borderRadius: '50%', background: color }} />;
}

function Tag({ label, tone }: { label: string; tone: 'local' | 'global' | 'new' }) {
  const palette = {
    local: { color: '#94a3b8', bg: 'rgba(148,163,184,0.12)', border: 'rgba(148,163,184,0.25)' },
    global: { color: 'var(--color-accent)', bg: 'rgba(99,102,241,0.12)', border: 'rgba(99,102,241,0.3)' },
    new: { color: '#f0a93b', bg: 'rgba(240,169,59,0.14)', border: 'rgba(240,169,59,0.35)' },
  }[tone];
  return (
    <span style={{ fontSize: '0.54rem', fontWeight: 800, letterSpacing: '0.05em', color: palette.color, background: palette.bg, border: `1px solid ${palette.border}`, borderRadius: 4, padding: '0px 4px' }}>
      {label}
    </span>
  );
}

const slotMono: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 5, fontFamily: 'monospace', fontSize: '0.72rem',
  color: '#e6edf6', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0,
};

const slotBox = (dragOver: boolean): React.CSSProperties => ({
  display: 'flex', flexDirection: 'column', gap: 3, padding: '5px 7px', borderRadius: 6,
  background: dragOver ? 'rgba(99,102,241,0.12)' : 'var(--bg-surface-opaque, #161b27)',
  border: `1px solid ${dragOver ? 'var(--color-accent)' : 'rgba(255,255,255,0.05)'}`,
});

function InputSlot({ decl, value, typeOf, onBind }: { decl: SubflowVariable; value: unknown; typeOf: (n: string) => VariableType | undefined; onBind?: (localName: string, value: unknown) => void }) {
  const { dragOver, handlers } = useSlotDrop(onBind ? (g) => onBind(decl.name, { __type: 'variable_ref', variableName: g.name }) : undefined);
  const boundGlobal = referencedGlobal(value);
  return (
    <div {...handlers} style={slotBox(dragOver)}>
      <div style={slotMono}>
        <TypeDot type={boundGlobal ? typeOf(boundGlobal) : decl.type} />
        {isDraggedRef(value)
          ? <span style={{ color: 'var(--color-accent)', overflow: 'hidden', textOverflow: 'ellipsis' }}>{boundGlobal}</span>
          : onBind
            ? <InlineInput value={typeof value === 'string' ? value : ''} placeholder="drop a global or type a value" onChange={(v) => onBind(decl.name, v)} />
            : <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{displayValue(value)}</span>}
      </div>
      <div style={{ ...slotMono, color: '#dbe4ee' }}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{decl.name}</span>
        <Tag label="LOCAL" tone="local" />
      </div>
    </div>
  );
}

function OutputSlot({ decl, global, isNew, onBind }: { decl: SubflowVariable; global: string; isNew: boolean; onBind?: (localName: string, globalName: string) => void }) {
  const { dragOver, handlers } = useSlotDrop(onBind ? (g) => onBind(decl.name, g.name) : undefined);
  return (
    <div {...handlers} style={slotBox(dragOver)}>
      <div style={slotMono}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{decl.name}</span>
        <Tag label="LOCAL" tone="local" />
      </div>
      <div style={slotMono}>
        <TypeDot type={isNew ? undefined : decl.type} />
        {onBind
          ? <InlineInput value={global} placeholder="global name" onChange={(v) => onBind(decl.name, v)} />
          : <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{global || '—'}</span>}
        <Tag label={isNew ? 'NEW' : 'GLOBAL'} tone={isNew ? 'new' : 'global'} />
      </div>
    </div>
  );
}

function Folder({ title, subtitle, count, children }: { title: string; subtitle: string; count: number; children: React.ReactNode }) {
  const [open, setOpen] = useState(true);
  return (
    <div className="nodrag nopan" style={{ flex: 1, minWidth: 128, border: '1px solid var(--border-color)', borderRadius: 8, background: 'rgba(255,255,255,0.02)', overflow: 'hidden' }}>
      <button
        className="nodrag"
        onClick={(e) => { e.stopPropagation(); setOpen((v) => !v); }}
        style={{ width: '100%', display: 'flex', alignItems: 'center', gap: 6, padding: '6px 8px', background: 'transparent', border: 'none', cursor: 'pointer', textAlign: 'left' }}
      >
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: '0.66rem', fontWeight: 800, color: '#dbe4ee', textTransform: 'uppercase', letterSpacing: '0.04em' }}>{title}</div>
          <div style={{ fontSize: '0.58rem', color: 'var(--text-muted)' }}>{subtitle}</div>
        </div>
        <span style={{ fontSize: '0.6rem', fontWeight: 700, color: 'var(--color-accent)', background: 'rgba(99,102,241,0.12)', borderRadius: 999, padding: '0px 6px' }}>{count}</span>
        <ChevronDown size={13} color="var(--text-muted)" style={{ transform: open ? 'none' : 'rotate(-90deg)', transition: 'transform 0.15s' }} />
      </button>
      {open && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4, padding: '0 8px 8px' }}>{children}</div>
      )}
    </div>
  );
}

function EmptyHint({ subject }: { subject: string }) {
  return (
    <div style={{ padding: '8px 6px', textAlign: 'center', fontSize: '0.62rem', color: 'var(--text-muted)', lineHeight: 1.4 }}>
      {subject}<br />declared.
    </div>
  );
}

export function SubflowLanes({ subflowName, inputs, outputs, variables, subflowInterface, onBindInput, onBindOutput }: SubflowLanesProps) {
  const typeOf = (name: string): VariableType | undefined => variables.find((v) => v.name === name)?.type;
  const known = (name: string) => variables.some((v) => v.name === name);
  const declaredInputs = subflowInterface?.inputs ?? [];
  const declaredOutputs = subflowInterface?.outputs ?? [];
  const noInterface = declaredInputs.length === 0 && declaredOutputs.length === 0;

  return (
    <div style={{ display: 'flex', alignItems: 'stretch', gap: 8, marginTop: 8, wordBreak: 'normal', overflowWrap: 'normal' }}>
      <Folder title="Inputs" subtitle="global → local" count={declaredInputs.length}>
        {declaredInputs.length > 0
          ? declaredInputs.map((decl) => (
              <InputSlot key={decl.name} decl={decl} value={inputs.find((r) => r.target === decl.name)?.value} typeOf={typeOf} onBind={onBindInput} />
            ))
          : <EmptyHint subject="No inputs" />}
      </Folder>

      <div style={{ flex: '0 0 auto', width: 96, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 4, padding: '8px 6px', borderRadius: 8, border: '1px dashed rgba(99,102,241,0.4)', background: 'repeating-linear-gradient(45deg, rgba(99,102,241,0.04) 0 6px, transparent 6px 12px)' }}>
        <span style={{ fontSize: '0.62rem', fontWeight: 800, color: 'var(--color-accent)', textTransform: 'uppercase', letterSpacing: '0.04em' }}>Local Scope</span>
        <span style={{ fontFamily: 'monospace', fontSize: '0.68rem', color: '#e6edf6', textAlign: 'center', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: '100%' }}>{subflowName || 'subflow'}</span>
        <span style={{ fontSize: '0.55rem', color: 'var(--text-muted)', textAlign: 'center' }}>
          {noInterface ? 'double-click to define I/O' : 'isolated copy'}
        </span>
      </div>

      <Folder title="Outputs" subtitle="local → global" count={declaredOutputs.length}>
        {declaredOutputs.length > 0
          ? declaredOutputs.map((decl) => {
              const bound = outputs.find((r) => r.source === decl.name)?.target;
              const globalName = typeof bound === 'string' ? bound : '';
              return (
                <OutputSlot key={decl.name} decl={decl} global={globalName} isNew={globalName.length > 0 && !known(globalName)} onBind={onBindOutput} />
              );
            })
          : <EmptyHint subject="No outputs" />}
      </Folder>
    </div>
  );
}
