// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect } from 'react'
import type { Dispatch, RefObject, SetStateAction } from 'react'
import type { Edge, Node as RFNode } from '@xyflow/react'
import { useVariableStore } from '../../stores/useVariableStore'

export interface UseCanvasKeyboardShortcutsArgs {
  clearClickConnect: () => void
  setSearchOpen: Dispatch<SetStateAction<boolean>>
  setShortcutsOpen: Dispatch<SetStateAction<boolean>>
  historyOpenRef: RefObject<boolean>
  /** True while a read-only version preview/diff is showing (so Escape can back out of it). */
  readOnlyRef: RefObject<boolean>
  /** True while an overlay that owns Escape is open (search/help/dialog) — suppresses the preview exit. */
  escOverlayOpenRef: RefObject<boolean>
  closeVersionOverview: () => void
  setHistoryOpen: Dispatch<SetStateAction<boolean>>
  doUndo: () => void
  doRedo: () => void
  recordUndo: () => void
  copySelection: () => boolean
  pasteClipboard: () => boolean
  duplicateSelection: () => boolean
  setNodes: Dispatch<SetStateAction<RFNode[]>>
  setEdges: Dispatch<SetStateAction<Edge[]>>
  setSelectedNode: Dispatch<SetStateAction<RFNode | null>>
  setSelectedEdge: Dispatch<SetStateAction<Edge | null>>
  nodesRef: RefObject<RFNode[]>
  edgesRef: RefObject<Edge[]>
  /** Dismisses the last run's node-status painting, returning the canvas to its normal look. */
  clearRunPainting: () => void
}

/**
 * Global keydown shortcuts for the canvas (Escape, Ctrl/⌘+F/K search, "?" help, Ctrl/⌘+Shift+H
 * history, undo/redo, copy/paste/duplicate, select-all, delete). Extracted from Canvas.tsx — a pure
 * aggregator over the other hooks' outputs; installs one window keydown listener.
 */
export function useCanvasKeyboardShortcuts(args: UseCanvasKeyboardShortcutsArgs): void {
  const {
    clearClickConnect, setSearchOpen, setShortcutsOpen, historyOpenRef, readOnlyRef, escOverlayOpenRef,
    closeVersionOverview, setHistoryOpen, doUndo, doRedo, recordUndo, copySelection, pasteClipboard,
    duplicateSelection, setNodes, setEdges, setSelectedNode, setSelectedEdge, nodesRef, edgesRef,
    clearRunPainting,
  } = args

  // Global keydown handler to support Delete / Backspace key deletions and Escape clearing
  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      const activeEl = document.activeElement;
      const editingText = !!activeEl && (
        activeEl.tagName === 'INPUT' ||
        activeEl.tagName === 'TEXTAREA' ||
        activeEl.tagName === 'SELECT' ||
        activeEl.getAttribute('contenteditable') === 'true'
      );

      if (event.key === 'Escape') {
        useVariableStore.getState().clearPins();
        clearClickConnect();
        // Escape is the canvas's general "put things back to normal", so it also dismisses the last
        // run's status painting. Safe to call unconditionally — it is a no-op when nothing is painted,
        // and it does not consume the key, so the preview/overlay handling below still runs.
        clearRunPainting();

        // Back out of a read-only version preview/diff, so "click a version to look, Escape to leave"
        // works like clicking outside the picker. Suppressed when a focused field or an overlay
        // (search / help / a dialog) owns Escape, or while the version menu itself is open (it reverts
        // its own transient preview on Escape). closeVersionOverview also closes the history drawer.
        if (!editingText && readOnlyRef.current && !escOverlayOpenRef.current) {
          closeVersionOverview();
        }
        return;
      }

      // Search / jump palette (Ctrl+F / Cmd+K). Allowed even from a field so the
      // palette can always be summoned; it owns its own input afterwards.
      if (
        (event.ctrlKey || event.metaKey) &&
        ((event.key === 'f' || event.key === 'F') || (event.key === 'k' || event.key === 'K'))
      ) {
        event.preventDefault();
        setSearchOpen(true);
        return;
      }

      // Keyboard-shortcut help ("?" = Shift+/). Ignored while typing in a field.
      if (!editingText && event.key === '?') {
        event.preventDefault();
        setShortcutsOpen((v) => !v);
        return;
      }

      // Version history drawer (Ctrl/⌘ + Shift + H). Shift avoids the bare
      // Ctrl+H browser-history / ⌘+H macOS-hide collisions. Allowed even from a
      // field so it can always be summoned.
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && (event.key === 'h' || event.key === 'H')) {
        event.preventDefault();
        if (historyOpenRef.current) {
          closeVersionOverview();
        } else {
          setHistoryOpen(true);
        }
        return;
      }

      // Undo / Redo (ignored while typing in a field).
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'z' || event.key === 'Z')) {
        event.preventDefault();
        if (event.shiftKey) doRedo();
        else doUndo();
        return;
      }
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'y' || event.key === 'Y')) {
        event.preventDefault();
        doRedo();
        return;
      }

      // Copy / Paste / Duplicate (ignored while typing in a field).
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'c' || event.key === 'C')) {
        if (copySelection()) event.preventDefault();
        return;
      }
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'v' || event.key === 'V')) {
        if (pasteClipboard()) event.preventDefault();
        return;
      }
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'd' || event.key === 'D')) {
        event.preventDefault(); // also stops the browser bookmark shortcut
        duplicateSelection();
        return;
      }

      // Select-all nodes (multi-select).
      if (!editingText && (event.ctrlKey || event.metaKey) && (event.key === 'a' || event.key === 'A')) {
        event.preventDefault();
        setNodes((nds) => nds.map((n) => ({ ...n, selected: true })));
        return;
      }

      if (event.key === 'Delete' || event.key === 'Backspace') {
        if (editingText) return;

        // Snapshot once before removing whatever is selected.
        if (nodesRef.current.some((n) => n.selected) || edgesRef.current.some((e) => e.selected)) {
          recordUndo();
        }

        // Delete selected nodes and their connected edges
        setNodes((nds) => {
          const selectedNodeIds = nds.filter((n) => n.selected).map((n) => n.id);
          if (selectedNodeIds.length > 0) {
            setEdges((eds) => eds.filter((e) => !selectedNodeIds.includes(e.source) && !selectedNodeIds.includes(e.target)));
            setSelectedNode(null);
            return nds.filter((n) => !selectedNodeIds.includes(n.id));
          }
          return nds;
        });

        // Delete selected edges
        setEdges((eds) => {
          const selectedEdgeIds = eds.filter((e) => e.selected).map((e) => e.id);
          if (selectedEdgeIds.length > 0) {
            setSelectedEdge(null);
            return eds.filter((e) => !selectedEdgeIds.includes(e.id));
          }
          return eds;
        });
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [setNodes, setEdges, clearClickConnect, doUndo, doRedo, recordUndo, copySelection, pasteClipboard, duplicateSelection, clearRunPainting]);
}
