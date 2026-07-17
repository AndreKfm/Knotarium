// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { CSSProperties } from 'react';
import { Handle, Position } from '@xyflow/react';
import { Radio, Zap } from 'lucide-react';
import {
  eventPinHandleId,
  actionPinHandleId,
  type DevicePin,
} from '../node-editor/externalDevicePins';

// Card body for the generic `externalDevice` node (firewall-clean — no provider names here).
//
// The block is a pure INBOUND surface: both selected events and selected incoming actions render as
// output (source) pins on the right edge — signals raised by the device that the graph reacts to.
// There are no input pins (sending a command is the separate Fire Action node). Each pin is a real
// React Flow Handle rendered inline with its label so wires anchor exactly to the row.

export interface ExternalDeviceLanesProps {
  /** Picked target's display label (which external device / instance). */
  targetLabel: string;
  events: DevicePin[];
  actions: DevicePin[];
  /** Snap-glow passthrough from the host node (keyed by handle id). */
  glowFor: (handleId: string) => CSSProperties;
  /** Accessibility props builder shared with the host node's ports. */
  portA11yProps: (label: string) => Record<string, unknown>;
  /** Host node display name, for port aria-labels. */
  displayName: string;
}

const EVENT_COLOR = 'var(--color-success)';
const ACTION_COLOR = 'var(--color-accent)';

const inlineHandleStyle = (color: string, glow: CSSProperties): CSSProperties => ({
  position: 'relative',
  left: 'auto',
  right: 'auto',
  top: 'auto',
  transform: 'none',
  width: 9,
  height: 9,
  background: color,
  borderColor: color,
  flex: 'none',
  ...glow,
});

const rowStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 6,
  padding: '4px 6px',
  borderRadius: 6,
  background: 'var(--bg-surface-opaque, #161b27)',
  border: '1px solid rgba(255,255,255,0.05)',
  minWidth: 0,
};

const labelStyle: CSSProperties = {
  fontFamily: 'monospace',
  fontSize: '0.72rem',
  color: '#e6edf6',
  whiteSpace: 'nowrap',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  minWidth: 0,
  flex: 1,
};

// One inbound group (Events or Incoming actions): right-aligned source pins. Every pin is a source
// Handle on the right edge — the device raises it, the graph reacts to it.
function SourceGroup({
  title,
  hint,
  icon,
  color,
  pins,
  handleIdFor,
  emptyText,
  ariaKind,
  glowFor,
  portA11yProps,
  displayName,
}: {
  title: string;
  hint: string;
  icon: React.ReactNode;
  color: string;
  pins: DevicePin[];
  handleIdFor: (value: string) => string;
  emptyText: string;
  ariaKind: string;
  glowFor: (handleId: string) => CSSProperties;
  portA11yProps: (label: string) => Record<string, unknown>;
  displayName: string;
}) {
  return (
    <div className="nodrag nopan" style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 5, justifyContent: 'flex-end' }}>
        <span style={{ fontSize: '0.55rem', fontWeight: 600, color: 'var(--text-muted)' }}>{hint}</span>
        <span style={{ fontSize: '0.6rem', fontWeight: 800, color, textTransform: 'uppercase', letterSpacing: '0.05em' }}>{title}</span>
        <span style={{ fontSize: '0.58rem', fontWeight: 700, color, background: 'rgba(255,255,255,0.06)', borderRadius: 999, padding: '0px 6px' }}>{pins.length}</span>
        {icon}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
        {pins.length > 0
          ? pins.map((pin) => {
              const handleId = handleIdFor(pin.value);
              return (
                <div key={handleId} style={{ ...rowStyle, justifyContent: 'flex-end' }} title={pin.value}>
                  <span style={{ ...labelStyle, textAlign: 'right' }}>{pin.label}</span>
                  <Handle
                    type="source"
                    position={Position.Right}
                    id={handleId}
                    style={inlineHandleStyle(color, glowFor(handleId))}
                    {...portA11yProps(`${displayName} ${ariaKind} ${pin.label} output`)}
                  />
                </div>
              );
            })
          : <span style={{ fontSize: '0.62rem', color: 'var(--text-muted)', textAlign: 'right', padding: '2px 4px' }}>{emptyText}</span>}
      </div>
    </div>
  );
}

export function ExternalDeviceLanes({ targetLabel, events, actions, glowFor, portA11yProps, displayName }: ExternalDeviceLanesProps) {
  const empty = events.length === 0 && actions.length === 0;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, marginTop: 8 }}>
      <SourceGroup
        title="Events" hint="from device ▸" icon={<Radio size={11} color={EVENT_COLOR} />} color={EVENT_COLOR}
        pins={events} handleIdFor={eventPinHandleId} emptyText="no events" ariaKind="event"
        glowFor={glowFor} portA11yProps={portA11yProps} displayName={displayName}
      />
      <SourceGroup
        title="Actions" hint="from device ▸" icon={<Zap size={11} color={ACTION_COLOR} />} color={ACTION_COLOR}
        pins={actions} handleIdFor={actionPinHandleId} emptyText="no actions" ariaKind="incoming action"
        glowFor={glowFor} portA11yProps={portA11yProps} displayName={displayName}
      />

      {empty && (
        <div style={{ fontSize: '0.62rem', color: 'var(--text-muted)', textAlign: 'center', lineHeight: 1.4, padding: '2px 4px' }}>
          {targetLabel ? 'Tick events / actions in the panel to add pins.' : 'Pick a device, then tick events / actions to add pins.'}
        </div>
      )}
    </div>
  );
}
