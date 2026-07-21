// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect, vi } from 'vitest';
import { useRef } from 'react';
import { render, fireEvent } from '@testing-library/react';
import type { Edge, Node as RFNode } from '@xyflow/react';
import { useCanvasKeyboardShortcuts } from './useCanvasKeyboardShortcuts';

/**
 * Minimal harness: drives the global keydown hook with controllable preview/overlay refs and a spy for
 * closeVersionOverview, so we can assert Escape backs out of a read-only version preview only when it
 * should. A focusable field lets us exercise the "field owns Escape" guard.
 */
function Harness({
  readOnly,
  overlayOpen,
  closeVersionOverview,
}: {
  readOnly: boolean;
  overlayOpen: boolean;
  closeVersionOverview: () => void;
}) {
  const readOnlyRef = useRef(readOnly);
  readOnlyRef.current = readOnly;
  const escOverlayOpenRef = useRef(overlayOpen);
  escOverlayOpenRef.current = overlayOpen;
  const historyOpenRef = useRef(false);
  const nodesRef = useRef<RFNode[]>([]);
  const edgesRef = useRef<Edge[]>([]);

  useCanvasKeyboardShortcuts({
    clearClickConnect: vi.fn(),
    setSearchOpen: vi.fn(),
    setShortcutsOpen: vi.fn(),
    historyOpenRef,
    readOnlyRef,
    escOverlayOpenRef,
    closeVersionOverview,
    setHistoryOpen: vi.fn(),
    doUndo: vi.fn(),
    doRedo: vi.fn(),
    recordUndo: vi.fn(),
    copySelection: () => false,
    pasteClipboard: () => false,
    duplicateSelection: () => false,
    setNodes: vi.fn(),
    setEdges: vi.fn(),
    setSelectedNode: vi.fn(),
    setSelectedEdge: vi.fn(),
    nodesRef,
    edgesRef,
  });

  return <textarea data-testid="field" defaultValue="x" />;
}

describe('useCanvasKeyboardShortcuts — Escape exits version preview', () => {
  it('exits the preview when read-only and no overlay is open', () => {
    const close = vi.fn();
    render(<Harness readOnly overlayOpen={false} closeVersionOverview={close} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(close).toHaveBeenCalledTimes(1);
  });

  it('does nothing when not previewing (editable draft)', () => {
    const close = vi.fn();
    render(<Harness readOnly={false} overlayOpen={false} closeVersionOverview={close} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(close).not.toHaveBeenCalled();
  });

  it('lets an open overlay own Escape instead of exiting the preview', () => {
    const close = vi.fn();
    render(<Harness readOnly overlayOpen closeVersionOverview={close} />);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(close).not.toHaveBeenCalled();
  });

  it('does not exit while a text field is focused (the field owns Escape)', () => {
    const close = vi.fn();
    const { getByTestId } = render(<Harness readOnly overlayOpen={false} closeVersionOverview={close} />);
    (getByTestId('field') as HTMLTextAreaElement).focus();
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(close).not.toHaveBeenCalled();
  });
});
