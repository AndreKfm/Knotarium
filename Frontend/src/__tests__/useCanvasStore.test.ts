// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { beforeEach, describe, expect, it } from 'vitest';
import { canvasPaletteStorageKey, maxRecentNodeIds, useCanvasStore } from '../stores/useCanvasStore';

describe('useCanvasStore', () => {
  beforeEach(() => {
    localStorage.clear();
    useCanvasStore.getState().resetPaletteState();
    useCanvasStore.persist.clearStorage();
  });

  it('togglePinNode adds and removes pinned node ids', () => {
    const { togglePinNode } = useCanvasStore.getState();

    togglePinNode('log');
    expect(useCanvasStore.getState().pinnedNodeIds).toEqual(['log']);

    togglePinNode('log');
    expect(useCanvasStore.getState().pinnedNodeIds).toEqual([]);
  });

  it('addRecentNode keeps a unique fifo list capped at five entries', () => {
    const { addRecentNode } = useCanvasStore.getState();

    ['start', 'log', 'httpRequest', 'condition', 'transform', 'delay', 'condition'].forEach(addRecentNode);

    expect(useCanvasStore.getState().recentNodeIds).toEqual([
      'log',
      'httpRequest',
      'transform',
      'delay',
      'condition',
    ]);
    expect(useCanvasStore.getState().recentNodeIds).toHaveLength(maxRecentNodeIds);
  });

  it('rehydrates pinned and recent ids from local storage', async () => {
    useCanvasStore.getState().resetPaletteState();

    localStorage.setItem(canvasPaletteStorageKey, JSON.stringify({
      state: {
        pinnedNodeIds: ['log', 'delay'],
        recentNodeIds: ['httpRequest'],
      },
      version: 0,
    }));

    await useCanvasStore.persist.rehydrate();

    expect(useCanvasStore.getState().pinnedNodeIds).toEqual(['log', 'delay']);
    expect(useCanvasStore.getState().recentNodeIds).toEqual(['httpRequest']);
  });
});