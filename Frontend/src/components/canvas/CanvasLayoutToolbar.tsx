// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { CSSProperties, Dispatch, SetStateAction } from 'react'
import type { FitViewOptions } from '@xyflow/react'
import { Crosshair, Maximize2, Hash, StickyNote, LayoutTemplate, Group, Combine, Ungroup, History, CircleHelp } from 'lucide-react'
import type { AlignEdge, DistributeAxis } from '../../node-editor/autoLayout'

// Floating layout-tools toolbar styles (Tidy + Align/Distribute).
const layoutToolbarStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '2px',
  padding: '4px',
  background: 'var(--bg-surface-opaque, #101625)',
  border: '1px solid var(--border-color)',
  borderRadius: '10px',
  boxShadow: '0 6px 20px rgba(0,0,0,0.3)',
}
const layoutBtnStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  gap: '4px',
  minWidth: '28px',
  height: '28px',
  padding: '0 8px',
  background: 'transparent',
  border: 'none',
  borderRadius: '6px',
  color: 'var(--text-primary, #e5e7eb)',
  fontSize: '0.9rem',
  cursor: 'pointer',
}
const layoutDividerStyle: CSSProperties = {
  width: '1px',
  height: '18px',
  background: 'var(--border-color)',
  margin: '0 2px',
}

export interface CanvasLayoutToolbarProps {
  selectedNodeCount: number
  readOnly: boolean
  extracting: boolean
  canUngroupSelection: boolean
  snapEnabled: boolean
  historyOpen: boolean
  alignSelection: (edge: AlignEdge) => void
  distributeSelection: (axis: DistributeAxis) => void
  fitView: (opts?: FitViewOptions) => void
  runAutoLayout: () => void
  setSnapEnabled: Dispatch<SetStateAction<boolean>>
  addStickyNote: () => void
  setTemplatePickerOpen: Dispatch<SetStateAction<boolean>>
  groupSelection: () => void
  extractToSubflow: () => void
  ungroupSelection: () => void
  closeVersionOverview: () => void
  setHistoryOpen: Dispatch<SetStateAction<boolean>>
  setShortcutsOpen: Dispatch<SetStateAction<boolean>>
}

/**
 * Floating top-right canvas toolbar: Align/Distribute (when ≥2 selected), Center, Tidy, Grid-snap,
 * Note, Template, Group/Extract/Ungroup, Version history, and the keyboard-shortcuts help button.
 * Presentational; extracted from Canvas.tsx.
 */
export function CanvasLayoutToolbar({
  selectedNodeCount, readOnly, extracting, canUngroupSelection, snapEnabled, historyOpen,
  alignSelection, distributeSelection, fitView, runAutoLayout, setSnapEnabled, addStickyNote,
  setTemplatePickerOpen, groupSelection, extractToSubflow, ungroupSelection, closeVersionOverview,
  setHistoryOpen, setShortcutsOpen,
}: CanvasLayoutToolbarProps) {
  return (
    <div
      style={{
        position: 'absolute',
        top: '12px',
        right: '12px',
        zIndex: 900,
        display: 'flex',
        gap: '8px',
        alignItems: 'flex-start',
      }}
    >
      {selectedNodeCount >= 2 && (
        <div style={layoutToolbarStyle}>
          <button type="button" style={layoutBtnStyle} title="Align left" onClick={() => alignSelection('left')}>⊢</button>
          <button type="button" style={layoutBtnStyle} title="Align horizontal centres" onClick={() => alignSelection('centerX')}>↔</button>
          <button type="button" style={layoutBtnStyle} title="Align right" onClick={() => alignSelection('right')}>⊣</button>
          <span style={layoutDividerStyle} />
          <button type="button" style={layoutBtnStyle} title="Align top" onClick={() => alignSelection('top')}>⊤</button>
          <button type="button" style={layoutBtnStyle} title="Align vertical centres" onClick={() => alignSelection('centerY')}>↕</button>
          <button type="button" style={layoutBtnStyle} title="Align bottom" onClick={() => alignSelection('bottom')}>⊥</button>
          {selectedNodeCount >= 3 && (
            <>
              <span style={layoutDividerStyle} />
              <button type="button" style={layoutBtnStyle} title="Distribute horizontally" onClick={() => distributeSelection('horizontal')}>⇿</button>
              <button type="button" style={layoutBtnStyle} title="Distribute vertically" onClick={() => distributeSelection('vertical')}>⇕</button>
            </>
          )}
        </div>
      )}
      <div className="lt-group">
        <button
          type="button"
          className="lt-btn"
          title="Center / fit all nodes in view"
          onClick={() => fitView({ padding: 0.15, duration: 400 })}
        >
          <span className="lt-btn-icon"><Crosshair size={15} /></span>
          Center
        </button>
        <button
          type="button"
          className="lt-btn"
          title="Tidy layout (auto-arrange left → right)"
          onClick={runAutoLayout}
        >
          <span className="lt-btn-icon"><Maximize2 size={15} /></span>
          Tidy
        </button>
        <button
          type="button"
          className={`lt-btn${snapEnabled ? ' lt-active' : ''}`}
          aria-pressed={snapEnabled}
          title={snapEnabled ? 'Snap to grid: on' : 'Snap to grid: off'}
          onClick={() => setSnapEnabled((v) => !v)}
        >
          <span className="lt-btn-icon"><Hash size={15} /></span>
          Grid
        </button>
        {!readOnly && (
          <button
            type="button"
            className="lt-btn"
            title="Add a sticky note"
            aria-label="Add a sticky note"
            onClick={addStickyNote}
          >
            <span className="lt-btn-icon"><StickyNote size={15} /></span>
            Note
          </button>
        )}
        {!readOnly && (
          <button
            type="button"
            className="lt-btn"
            title="Insert a template's nodes into this workflow"
            aria-label="Insert from template"
            onClick={() => setTemplatePickerOpen(true)}
          >
            <span className="lt-btn-icon"><LayoutTemplate size={15} /></span>
            Template
          </button>
        )}
        {!readOnly && selectedNodeCount >= 2 && (
          <button
            type="button"
            className="lt-btn"
            title="Group selected nodes"
            aria-label="Group selected nodes"
            onClick={groupSelection}
          >
            <span className="lt-btn-icon"><Group size={15} /></span>
            Group
          </button>
        )}
        {!readOnly && selectedNodeCount >= 1 && (
          <button
            type="button"
            className="lt-btn"
            title="Extract the selected nodes into a new subflow"
            aria-label="Extract selection to a subflow"
            onClick={extractToSubflow}
            disabled={extracting}
          >
            <span className="lt-btn-icon"><Combine size={15} /></span>
            {extracting ? 'Extracting…' : 'Extract'}
          </button>
        )}
        {!readOnly && canUngroupSelection && (
          <button
            type="button"
            className="lt-btn"
            title="Ungroup"
            aria-label="Ungroup"
            onClick={ungroupSelection}
          >
            <span className="lt-btn-icon"><Ungroup size={15} /></span>
            Ungroup
          </button>
        )}
        <button
          type="button"
          className={`lt-btn${historyOpen ? ' lt-active' : ''}`}
          aria-pressed={historyOpen}
          title="Version history (Ctrl/⌘+Shift+H)"
          aria-label="Version history"
          onClick={() => (historyOpen ? closeVersionOverview() : setHistoryOpen(true))}
        >
          <span className="lt-btn-icon"><History size={15} /></span>
          History
        </button>
        <span className="lt-divider" />
        <button
          type="button"
          className="lt-btn lt-help"
          title="Keyboard shortcuts (?)"
          aria-label="Keyboard shortcuts"
          onClick={() => setShortcutsOpen(true)}
        >
          <CircleHelp size={16} />
        </button>
      </div>
    </div>
  )
}
