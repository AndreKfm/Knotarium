/**
 * WorkflowDefinitions.tsx
 * ---------------------------------------------------------------------------
 * Drop-in React component for the Knotarium "Workflow Definitions" panel.
 * Adds: inline rename, collapsible groups (create / rename / delete),
 * per-card group reassignment, drag & drop (reorder + move between groups),
 * Comfortable/Compact density toggle, color dot swatch palette popover,
 * and deletion confirmation modal backdrop.
 */

import React, { useState, useRef, useEffect } from 'react';
import type { WorkflowDefinition, WorkflowGroup, NotificationChannel, FailureAlertConfig, FailureAlertMode } from '../types';
import { useInjectStyles } from './WorkflowDefinitions.styles';

const DEFAULT_DENSITY = 'compact'; // 'compact' | 'comfortable'
const GROUP_COLORS = ['#34d399', '#a78bfa', '#22d3ee', '#f0b429', '#f0556d', '#60a5fa'];

/* ----------------------------- icons ----------------------------- */
const I = {
  plus: <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round"><path d="M12 5v14M5 12h14" /></svg>,
  chev: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"><path d="M6 9l6 6 6-6" /></svg>,
  pencil: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 20h9M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z" /></svg>,
  grip: <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor"><circle cx="9" cy="6" r="1.6" /><circle cx="15" cy="6" r="1.6" /><circle cx="9" cy="12" r="1.6" /><circle cx="15" cy="12" r="1.6" /><circle cx="9" cy="18" r="1.6" /><circle cx="15" cy="18" r="1.6" /></svg>,
  eye: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z" /><circle cx="12" cy="12" r="3" /></svg>,
  play: <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="M7 5l12 7-12 7z" /></svg>,
  check: <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round"><path d="M5 13l4 4L19 7" /></svg>,
  trash: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2m2 0v14a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V6" /></svg>,
  power: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 2v10" /><path d="M18.4 6.6a9 9 0 1 1-12.8 0" /></svg>,
  bookmark: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z" /></svg>,
  copy: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="9" y="9" width="11" height="11" rx="2" /><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" /></svg>,
  bell: <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9" /><path d="M13.73 21a2 2 0 0 1-3.46 0" /></svg>,
};


/* ----------------------------- pieces ----------------------------- */
interface NameEditorProps {
  value: string;
  fontClass: string;
  onSave: (v: string) => void;
  onCancel: () => void;
}

function NameEditor({ value, fontClass, onSave, onCancel }: NameEditorProps) {
  const ref = useRef<HTMLInputElement>(null);
  const [val, setVal] = useState(value);

  useEffect(() => {
    if (ref.current) {
      ref.current.focus();
      ref.current.select();
    }
  }, []);

  return (
    <div className="kwf-namerow">
      <input
        ref={ref}
        className={fontClass}
        value={val}
        onChange={(e) => setVal(e.target.value)}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          if (e.key === 'Enter') onSave(val.trim() || value);
          if (e.key === 'Escape') onCancel();
        }}
      />
      <div className="kwf-editactions">
        <button className="kwf-mini kwf-mini-save" onClick={() => onSave(val.trim() || value)}>
          {I.check} Save
        </button>
        <button className="kwf-mini kwf-mini-cancel" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </div>
  );
}

interface GroupChipProps {
  wf: WorkflowDefinition;
  groups: WorkflowGroup[];
  onAssign: (id: string, group: string | null) => void;
  mini?: boolean;
}

function GroupChip({ wf, groups, onAssign, mini }: GroupChipProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const h = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', h);
    return () => document.removeEventListener('mousedown', h);
  }, []);

  const currentGroupId = wf.metadata?.group ?? null;
  const cur = groups.find((g) => g.id === currentGroupId);

  return (
    <div className="kwf-chipwrap" ref={ref}>
      <button
        className={'kwf-chip' + (mini ? ' kwf-mini' : '')}
        onClick={(e) => {
          e.stopPropagation();
          setOpen((o) => !o);
        }}
      >
        <span className="kwf-cdot" style={{ background: cur ? cur.color : '#3a4759' }} />
        {cur ? cur.name : 'Ungrouped'}
        <span className="kwf-cchev">{I.chev}</span>
      </button>
      {open && (
        <div className="kwf-menu" onClick={(e) => e.stopPropagation()}>
          <div className="kwf-mlabel">MOVE TO GROUP</div>
          {groups.map((g) => (
            <div
              key={g.id}
              className="kwf-mitem"
              onClick={() => {
                onAssign(wf.id.value, g.id);
                setOpen(false);
              }}
            >
              <span className="kwf-mdot" style={{ background: g.color }} />
              {g.name}
              {currentGroupId === g.id && <span className="kwf-mcheck">{I.check}</span>}
            </div>
          ))}
          <div className="kwf-msep" />
          <div
            className="kwf-mitem"
            onClick={() => {
              onAssign(wf.id.value, null);
              setOpen(false);
            }}
          >
            <span className="kwf-mdot" style={{ background: '#3a4759' }} />
            Ungrouped
            {!currentGroupId && <span className="kwf-mcheck">{I.check}</span>}
          </div>
        </div>
      )}
    </div>
  );
}

interface FailureAlertChipProps {
  wf: WorkflowDefinition;
  channels: NotificationChannel[];
  onSet: (id: string, config: FailureAlertConfig) => void;
  mini?: boolean;
}

const ALERT_MODE_LABELS: Record<FailureAlertMode, string> = {
  Inherit: 'Alerts: Default',
  Off: 'Alerts: Off',
  Custom: 'Alerts: Custom',
};

function FailureAlertChip({ wf, channels, onSet, mini }: FailureAlertChipProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const h = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', h);
    return () => document.removeEventListener('mousedown', h);
  }, []);

  const config = wf.metadata?.failureAlert ?? null;
  const mode: FailureAlertMode = config?.mode ?? 'Inherit';
  const selectedIds = config?.channelIds ?? [];

  const setMode = (next: FailureAlertMode) => {
    onSet(wf.id.value, { mode: next, channelIds: next === 'Custom' ? selectedIds : [] });
  };

  const toggleChannel = (channelId: string) => {
    const next = selectedIds.includes(channelId)
      ? selectedIds.filter((id) => id !== channelId)
      : [...selectedIds, channelId];
    onSet(wf.id.value, { mode: 'Custom', channelIds: next });
  };

  const dotColor = mode === 'Off' ? '#f0556d' : mode === 'Custom' ? '#a78bfa' : '#34d399';

  return (
    <div className="kwf-chipwrap" ref={ref}>
      <button
        className={'kwf-chip' + (mini ? ' kwf-mini' : '')}
        title="Configure failure alerts for this workflow"
        onClick={(e) => {
          e.stopPropagation();
          setOpen((o) => !o);
        }}
      >
        <span className="kwf-cdot" style={{ background: dotColor }} />
        {mini ? <span style={{ display: 'inline-flex' }}>{I.bell}</span> : ALERT_MODE_LABELS[mode]}
        <span className="kwf-cchev">{I.chev}</span>
      </button>
      {open && (
        <div className="kwf-menu" onClick={(e) => e.stopPropagation()}>
          <div className="kwf-mlabel">FAILURE ALERTS</div>
          <div className="kwf-mitem" onClick={() => setMode('Inherit')}>
            <span className="kwf-mdot" style={{ background: '#34d399' }} />
            Default channels
            {mode === 'Inherit' && <span className="kwf-mcheck">{I.check}</span>}
          </div>
          <div className="kwf-mitem" onClick={() => setMode('Off')}>
            <span className="kwf-mdot" style={{ background: '#f0556d' }} />
            Off (no alerts)
            {mode === 'Off' && <span className="kwf-mcheck">{I.check}</span>}
          </div>
          <div className="kwf-mitem" onClick={() => setMode('Custom')}>
            <span className="kwf-mdot" style={{ background: '#a78bfa' }} />
            Custom channels
            {mode === 'Custom' && <span className="kwf-mcheck">{I.check}</span>}
          </div>
          {mode === 'Custom' && (
            <>
              <div className="kwf-msep" />
              {channels.length === 0 ? (
                <div className="kwf-mitem" style={{ opacity: 0.6, cursor: 'default' }}>No channels configured</div>
              ) : (
                channels.map((c) => (
                  <div key={c.id} className="kwf-mitem" onClick={() => toggleChannel(c.id)}>
                    <span className="kwf-mdot" style={{ background: '#a78bfa' }} />
                    {c.name}
                    {selectedIds.includes(c.id) && <span className="kwf-mcheck">{I.check}</span>}
                  </div>
                ))
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

interface CardProps {
  wf: WorkflowDefinition;
  groups: WorkflowGroup[];
  channels: NotificationChannel[];
  onSetFailureAlert: (id: string, config: FailureAlertConfig) => void;
  compact: boolean;
  dragging: boolean;
  dropTarget: boolean;
  onRename: (id: string, name: string) => void;
  onAssign: (id: string, group: string | null) => void;
  onViewGraph: (id: string) => void;
  onTriggerRun: (id: string) => void;
  onToggleEnabled: (id: string, enabled: boolean) => void;
  onDeleteWorkflow: (wf: WorkflowDefinition) => void;
  onSaveAsTemplate: (wf: WorkflowDefinition) => void;
  onDuplicate: (id: string) => void;
  onDragStart: (id: string) => void;
  onDragEnd: () => void;
  onDragOverCard: (id: string) => void;
  onDropCard: (id: string) => void;
}

function Card({
  wf,
  groups,
  channels,
  onSetFailureAlert,
  compact,
  dragging,
  dropTarget,
  onRename,
  onAssign,
  onViewGraph,
  onTriggerRun,
  onToggleEnabled,
  onDeleteWorkflow,
  onSaveAsTemplate,
  onDuplicate,
  onDragStart,
  onDragEnd,
  onDragOverCard,
  onDropCard,
}: CardProps) {
  const [editing, setEditing] = useState(false);
  const enabled = wf.isEnabled !== false;

  const dragProps = {
    draggable: !editing,
    onDragStart: (e: React.DragEvent) => {
      e.stopPropagation();
      onDragStart(wf.id.value);
    },
    onDragEnd,
    onDragOver: (e: React.DragEvent) => {
      e.preventDefault();
      e.stopPropagation();
      onDragOverCard(wf.id.value);
    },
    onDrop: (e: React.DragEvent) => {
      e.preventDefault();
      e.stopPropagation();
      onDropCard(wf.id.value);
    },
  };

  const cls = (base: string) => base + (dragging ? ' kwf-dragging' : '') + (dropTarget ? ' kwf-drop' : '');

  const nodesCount = wf.nodes?.length ?? 0;
  const connsCount = wf.edges?.length ?? 0;

  // Clicking the card opens the canvas — but ignore clicks that land on an interactive control
  // (buttons, the name input, group/alert chips, the drag grip) so those keep their own behavior.
  const handleCardOpen = (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest('button, input, .kwf-chipwrap, .kwf-grip')) {
      return;
    }
    onViewGraph(wf.id.value);
  };

  const nameBlock = editing ? (
    <NameEditor
      value={wf.name}
      fontClass="kwf-name-input"
      onSave={(v) => {
        onRename(wf.id.value, v);
        setEditing(false);
      }}
      onCancel={() => setEditing(false)}
    />
  ) : (
    <div className="kwf-namerow">
      <span className="kwf-name" title={wf.name || 'Unnamed Workflow'}>{wf.name || 'Unnamed Workflow'}</span>
      <button
        className="kwf-editbtn"
        title="Rename"
        onClick={(e) => {
          e.stopPropagation();
          setEditing(true);
        }}
      >
        {I.pencil}
      </button>
    </div>
  );

  if (compact) {
    return (
      <div className={cls('kwf-card kwf-compact') + (enabled ? '' : ' kwf-inactive')} title={'ID: ' + wf.id.value} onClick={handleCardOpen} style={{ cursor: 'pointer' }} {...dragProps}>
        <span className="kwf-grip" title="Drag to reorder or move between groups">
          {I.grip}
        </span>
        <div className="kwf-namearea">{nameBlock}</div>
        {!enabled && <span className="kwf-inactive-badge">Inactive</span>}
        <span className="kwf-cmeta">
          {nodesCount} nodes · {connsCount} conn
        </span>
        <div className="kwf-csecondary">
        <GroupChip wf={wf} groups={groups} onAssign={onAssign} mini />
        <FailureAlertChip wf={wf} channels={channels} onSet={onSetFailureAlert} mini />
        <div className="kwf-cactions">
          <button
            className={'kwf-iconbtn kwf-ghost' + (enabled ? ' kwf-pwr-on' : ' kwf-pwr-off')}
            title={enabled ? 'Active — click to deactivate (stops triggers & running executions)' : 'Inactive — click to activate'}
            onClick={(e) => { e.stopPropagation(); onToggleEnabled(wf.id.value, !enabled); }}
          >
            {I.power}
          </button>
          <button
            className="kwf-iconbtn kwf-ghost kwf-del"
            title="Delete workflow"
            onClick={(e) => { e.stopPropagation(); onDeleteWorkflow(wf); }}
          >
            {I.trash}
          </button>
          <button className="kwf-iconbtn kwf-ghost" title="View Graph" onClick={() => onViewGraph(wf.id.value)}>
            {I.eye}
          </button>
          <button
            className="kwf-iconbtn kwf-ghost"
            title="Save as a reusable template in your library"
            onClick={(e) => { e.stopPropagation(); onSaveAsTemplate(wf); }}
          >
            {I.bookmark}
          </button>
          <button
            className="kwf-iconbtn kwf-ghost"
            title="Duplicate this workflow into a new (copy) draft"
            onClick={(e) => { e.stopPropagation(); onDuplicate(wf.id.value); }}
          >
            {I.copy}
          </button>
          <button
            className="kwf-iconbtn kwf-run"
            title={wf.hasActiveVersion ? "Trigger Run" : "Publish and activate this workflow before running"}
            disabled={!wf.hasActiveVersion}
            onClick={() => onTriggerRun(wf.id.value)}
          >
            {I.play}
          </button>
        </div>
        </div>
      </div>
    );
  }

  return (
    <div className={cls('kwf-card') + (enabled ? '' : ' kwf-inactive')} onClick={handleCardOpen} style={{ cursor: 'pointer' }} {...dragProps}>
      <div className="kwf-ctop">
        <span className="kwf-grip" title="Drag to reorder or move between groups">
          {I.grip}
        </span>
        <div className="kwf-namearea">
          {nameBlock}
          <div className="kwf-id">ID: {wf.id.value}</div>
        </div>
        <div className="kwf-pills">
          {!enabled && <span className="kwf-pill kwf-inactive-badge">Inactive</span>}
          <span className="kwf-pill">{nodesCount} Nodes</span>
          <span className="kwf-pill">{connsCount} Connections</span>
        </div>
      </div>
      <div className="kwf-cfoot">
        <GroupChip wf={wf} groups={groups} onAssign={onAssign} />
        <FailureAlertChip wf={wf} channels={channels} onSet={onSetFailureAlert} />
        <div className="kwf-actions">
          <button
            className={'kwf-act kwf-act-ghost' + (enabled ? ' kwf-pwr-on' : ' kwf-pwr-off')}
            title={enabled ? 'Active — click to deactivate (stops triggers & running executions)' : 'Inactive — click to activate'}
            onClick={(e) => { e.stopPropagation(); onToggleEnabled(wf.id.value, !enabled); }}
          >
            {I.power} {enabled ? 'Active' : 'Inactive'}
          </button>
          <button
            className="kwf-act kwf-act-del"
            title="Delete workflow"
            onClick={(e) => { e.stopPropagation(); onDeleteWorkflow(wf); }}
          >
            {I.trash} Delete
          </button>
          <button className="kwf-act kwf-act-ghost" onClick={() => onViewGraph(wf.id.value)}>
            {I.eye} Edit / View
          </button>
          <button
            className="kwf-act kwf-act-ghost"
            title="Save as a reusable template in your library"
            onClick={(e) => { e.stopPropagation(); onSaveAsTemplate(wf); }}
          >
            {I.bookmark} Save as template
          </button>
          <button
            className="kwf-act kwf-act-run"
            title={wf.hasActiveVersion ? "Trigger Run" : "Publish and activate this workflow before running"}
            disabled={!wf.hasActiveVersion}
            onClick={() => onTriggerRun(wf.id.value)}
          >
            {I.play} Trigger Run
          </button>
        </div>
      </div>
    </div>
  );
}

/* ----------------------------- Swatch picker ----------------------------- */
interface SwatchPickerProps {
  currentHidden: boolean;
  currentColor: string;
  onSelectColor: (color: string) => void;
  onClose: () => void;
}

function SwatchPicker({ currentColor, onSelectColor, onClose }: SwatchPickerProps) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleOutsideClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        onClose();
      }
    };
    document.addEventListener('mousedown', handleOutsideClick);
    return () => document.removeEventListener('mousedown', handleOutsideClick);
  }, [onClose]);

  return (
    <div className="kwf-swatch-popover" ref={ref} onClick={(e) => e.stopPropagation()}>
      {GROUP_COLORS.map((c) => (
        <span
          key={c}
          className={'kwf-swatch-dot' + (c === currentColor ? ' kwf-active-swatch' : '')}
          style={{ background: c }}
          onClick={() => {
            onSelectColor(c);
            onClose();
          }}
        />
      ))}
    </div>
  );
}

/* ----------------------------- main ----------------------------- */
interface WorkflowDefinitionsProps {
  workflows: WorkflowDefinition[];
  groups: WorkflowGroup[];
  channels?: NotificationChannel[];
  onSetFailureAlert?: (id: string, config: FailureAlertConfig) => void;
  onRenameWorkflow: (id: string, name: string) => void;
  onMoveWorkflow: (id: string, arg: { group: string | null; beforeId: string | null }) => void;
  onCreateGroup: (name: string, color: string) => string | Promise<string>;
  onRenameGroup: (id: string, name: string) => void;
  onUpdateGroupColor: (id: string, color: string) => void;
  onDeleteGroup: (id: string) => void;
  onDeleteWorkflow: (id: string) => void;
  onViewGraph: (id: string) => void;
  onTriggerRun: (id: string) => void;
  onToggleEnabled: (id: string, enabled: boolean) => void;
  onSaveAsTemplate?: (id: string) => void;
  onDuplicate?: (id: string) => void;
  /** When a name filter is active, hide groups with no matches (only groups with hits are useful). */
  isFiltering?: boolean;
}

export default function WorkflowDefinitions({
  workflows = [],
  groups = [],
  channels = [],
  onSetFailureAlert = () => {},
  onRenameWorkflow = () => {},
  onMoveWorkflow = () => {},
  onCreateGroup = () => '',
  onRenameGroup = () => {},
  onUpdateGroupColor = () => {},
  onDeleteGroup = () => {},
  onDeleteWorkflow = () => {},
  onViewGraph = () => {},
  onTriggerRun = () => {},
  onToggleEnabled = () => {},
  onSaveAsTemplate = () => {},
  onDuplicate = () => {},
  isFiltering = false,
}: WorkflowDefinitionsProps) {
  useInjectStyles();
  const [density, setDensity] = useState<'compact' | 'comfortable'>(DEFAULT_DENSITY);
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  // Empty named groups fold into one quiet "N empty groups" shelf instead of a stack of dead rows.
  const [emptyShelfOpen, setEmptyShelfOpen] = useState(false);
  const [editingGroup, setEditingGroup] = useState<string | undefined>(undefined);
  const [dragId, setDragId] = useState<string | null>(null);
  const [dragOverGroup, setDragOverGroup] = useState<string | null>(null);
  const [dragOverId, setDragOverId] = useState<string | null>(null);

  // Swatch configuration
  const [activeSwatchGroupId, setActiveSwatchGroupId] = useState<string | null>(null);

  // Deletion modal configuration
  const [groupToDelete, setGroupToDelete] = useState<WorkflowGroup | null>(null);
  const [workflowToDelete, setWorkflowToDelete] = useState<WorkflowDefinition | null>(null);

  const clearDrag = () => {
    setDragId(null);
    setDragOverGroup(null);
    setDragOverId(null);
  };

  const dropOnGroup = (group: string | null) => {
    if (dragId) {
      onMoveWorkflow(dragId, { group, beforeId: null });
    }
    clearDrag();
  };

  const dropOnCard = (targetId: string) => {
    if (!dragId || dragId === targetId) {
      clearDrag();
      return;
    }
    const target = workflows.find((x) => x.id.value === targetId);
    if (target) {
      onMoveWorkflow(dragId, { group: target.metadata?.group ?? null, beforeId: targetId });
    }
    clearDrag();
  };

  const handleCreateGroup = async () => {
    const color = GROUP_COLORS[groups.length % GROUP_COLORS.length];
    const newId = await onCreateGroup('New Group', color);
    if (newId) {
      setEditingGroup(newId); // focus rename if parent returns the id
    }
  };

  const handleDeleteGroupClick = (group: WorkflowGroup) => {
    const count = workflows.filter((x) => (x.metadata?.group ?? null) === group.id).length;
    if (count === 0) {
      // Empty Group: Silent deletion.
      onDeleteGroup(group.id);
    } else {
      // Non-Empty Group: Light modal confirmation portal.
      setGroupToDelete(group);
    }
  };

  const confirmDeleteGroup = () => {
    if (groupToDelete) {
      onDeleteGroup(groupToDelete.id);
      setGroupToDelete(null);
    }
  };

  const sections = [
    ...groups.map((g) => ({ ...g, ungrouped: false })),
    { id: null as unknown as string, name: 'Ungrouped', color: '#3a4759', ungrouped: true },
  ];

  type Section = (typeof sections)[number];

  const itemsOf = (g: Section) =>
    workflows.filter((wf) => {
      const gId = wf.metadata?.group ?? null;
      if (g.ungrouped) {
        // Ungrouped if it has no group, or references a group that no longer exists (orphan reference).
        return gId === null || !groups.some((grp) => grp.id === gId);
      }
      return gId === g.id;
    });

  // Render one group (bar + card body). Split out so the empty-group shelf can reuse it verbatim,
  // preserving every drop/rename/delete/collapse behaviour when the shelf is expanded.
  const renderGroup = (g: Section, items: WorkflowDefinition[]) => {
    const isCol = collapsed[g.id ?? 'null'];
    const overThis = dragOverGroup === g.id;

    return (
      <div className="kwf-group" key={g.id || '__ungrouped'}>
        <div
          className={'kwf-gbar' + (overThis && dragId ? ' kwf-dragover' : '')}
          onClick={() => setCollapsed((c) => ({ ...c, [g.id ?? 'null']: !c[g.id ?? 'null'] }))}
          onDragOver={(e) => {
            e.preventDefault();
            setDragOverGroup(g.id);
            setDragOverId(null);
          }}
          onDragLeave={() => setDragOverGroup((d) => (d === g.id ? null : d))}
          onDrop={() => dropOnGroup(g.id)}
        >
          <span className={'kwf-gchev' + (isCol ? ' kwf-col' : '')}>{I.chev}</span>
          <span className="kwf-gdot-container">
            <span
              className="kwf-gdot"
              style={{ background: g.color }}
              onClick={(e) => {
                if (g.ungrouped) return;
                e.stopPropagation();
                setActiveSwatchGroupId((o) => (o === g.id ? null : g.id));
              }}
            />
            {!g.ungrouped && activeSwatchGroupId === g.id && (
              <SwatchPicker
                currentHidden={false}
                currentColor={g.color}
                onSelectColor={(color) => onUpdateGroupColor(g.id, color)}
                onClose={() => setActiveSwatchGroupId(null)}
              />
            )}
          </span>
          {!g.ungrouped && editingGroup === g.id ? (
            <input
              className="kwf-gname-input"
              autoFocus
              defaultValue={g.name}
              onClick={(e) => e.stopPropagation()}
              onBlur={(e) => {
                onRenameGroup(g.id, e.target.value.trim() || g.name);
                setEditingGroup(undefined);
              }}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  onRenameGroup(g.id, e.currentTarget.value.trim() || g.name);
                  setEditingGroup(undefined);
                }
                if (e.key === 'Escape') setEditingGroup(undefined);
              }}
            />
          ) : (
            <span className="kwf-gname">{g.name}</span>
          )}
          <span className="kwf-gcount">{items.length}</span>
          {!g.ungrouped && (
            <div className="kwf-gactions" onClick={(e) => e.stopPropagation()}>
              <button className="kwf-gact" title="Rename group" onClick={() => setEditingGroup(g.id)}>
                {I.pencil}
              </button>
              <button className="kwf-gact" title="Delete group" onClick={() => handleDeleteGroupClick(g as WorkflowGroup)}>
                {I.trash}
              </button>
            </div>
          )}
        </div>

        {!isCol && items.length > 0 && (
          <div
            className={'kwf-gbody' + (overThis && dragOverId === null && dragId ? ' kwf-append' : '')}
            onDragOver={(e) => {
              e.preventDefault();
              setDragOverGroup(g.id);
              setDragOverId(null);
            }}
            onDrop={(e) => {
              e.preventDefault();
              dropOnGroup(g.id);
            }}
          >
            {items.map((x) => (
              <Card
                key={x.id.value}
                wf={x}
                groups={groups}
                channels={channels}
                onSetFailureAlert={onSetFailureAlert}
                compact={density === 'compact'}
                dragging={dragId === x.id.value}
                dropTarget={dragOverId === x.id.value && dragId !== null && dragId !== x.id.value}
                onRename={onRenameWorkflow}
                onAssign={(id, group) => onMoveWorkflow(id, { group, beforeId: null })}
                onViewGraph={onViewGraph}
                onTriggerRun={onTriggerRun}
                onToggleEnabled={onToggleEnabled}
                onDeleteWorkflow={setWorkflowToDelete}
                onSaveAsTemplate={(w) => onSaveAsTemplate(w.id.value)}
                onDuplicate={onDuplicate}
                onDragStart={(id) => setDragId(id)}
                onDragEnd={clearDrag}
                onDragOverCard={(id) => {
                  setDragOverId(id);
                  setDragOverGroup(g.id);
                }}
                onDropCard={dropOnCard}
              />
            ))}
          </div>
        )}
      </div>
    );
  };

  // Partition the visible sections: groups with workflows render inline; empty NAMED groups fold into a
  // single shelf. Empty Ungrouped is always dropped; while filtering, empty named groups are dropped too
  // (only groups with matches are useful mid-search).
  const visibleSections = sections
    .map((g) => ({ g, items: itemsOf(g) }))
    .filter(({ g, items }) => !(items.length === 0 && (g.ungrouped || isFiltering)));
  const filledSections = visibleSections.filter(({ items }) => items.length > 0);
  const emptySections = visibleSections.filter(({ items }) => items.length === 0);
  // Dragging must reveal empty groups so they stay valid drop targets.
  const shelfExpanded = emptyShelfOpen || dragId !== null;

  return (
    <div className="kwf-root">
      <div className="kwf-toolbar">
        <div className="kwf-density">
          <button className={density === 'comfortable' ? 'kwf-active' : ''} onClick={() => setDensity('comfortable')}>
            Comfortable
          </button>
          <button className={density === 'compact' ? 'kwf-active' : ''} onClick={() => setDensity('compact')}>
            Compact
          </button>
        </div>
        <button className="kwf-newgroup" onClick={handleCreateGroup}>
          {I.plus} New Group
        </button>
      </div>

      {filledSections.map(({ g, items }) => renderGroup(g, items))}

      {emptySections.length > 0 && (
        <div className={'kwf-emptyshelf' + (shelfExpanded ? ' kwf-open' : '')}>
          <div className="kwf-emptybar" onClick={() => setEmptyShelfOpen((o) => !o)}>
            <span className="kwf-emptydots">
              {emptySections.map(({ g }) => (
                <i key={g.id || '__ungrouped'} style={{ background: g.color }} />
              ))}
            </span>
            {emptySections.length} empty group{emptySections.length === 1 ? '' : 's'}
            <span className="kwf-emptycaret">{I.chev}</span>
          </div>
          {shelfExpanded && (
            <div className="kwf-emptybody">
              {emptySections.map(({ g, items }) => renderGroup(g, items))}
            </div>
          )}
        </div>
      )}

      {/* Deletion Safety Confirmation Portal Backdrop Modal */}
      {groupToDelete && (
        <div className="kwf-modal-overlay" onClick={() => setGroupToDelete(null)}>
          <div className="kwf-modal-box" onClick={(e) => e.stopPropagation()}>
            <div className="kwf-modal-title">Delete "{groupToDelete.name}"?</div>
            <div className="kwf-modal-body">
              This group is not empty. Delete "{groupToDelete.name}"? Its{' '}
              {workflows.filter((x) => (x.metadata?.group ?? null) === groupToDelete.id).length} workflows will move to
              Ungrouped.
            </div>
            <div className="kwf-modal-actions">
              <button className="kwf-btn kwf-btn-cancel" onClick={() => setGroupToDelete(null)}>
                Cancel
              </button>
              <button className="kwf-btn kwf-btn-danger" onClick={confirmDeleteGroup}>
                Delete & Reassign
              </button>
            </div>
          </div>
        </div>
      )}

      {workflowToDelete && (
        <div className="kwf-modal-overlay" onClick={() => setWorkflowToDelete(null)}>
          <div className="kwf-modal-box" onClick={(e) => e.stopPropagation()}>
            <div className="kwf-modal-title">Delete "{workflowToDelete.name || 'Unnamed Workflow'}"?</div>
            <div className="kwf-modal-body">
              This will permanently delete the workflow and all its versions. This action cannot be undone.
            </div>
            <div className="kwf-modal-actions">
              <button className="kwf-btn kwf-btn-cancel" onClick={() => setWorkflowToDelete(null)}>
                Cancel
              </button>
              <button
                className="kwf-btn kwf-btn-danger"
                onClick={() => {
                  onDeleteWorkflow(workflowToDelete.id.value);
                  setWorkflowToDelete(null);
                }}
              >
                Delete Workflow
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
