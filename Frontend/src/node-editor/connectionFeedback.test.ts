// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { connectionFailureReason, type ConnectionDropContext } from './connectionFeedback';

const base: ConnectionDropContext = {
  fromHandleType: 'source',
  fromNodeId: 'a',
  toNodeId: 'b',
  toNodeIsContainer: false,
  toNodeHasInput: true,
};

describe('connectionFailureReason', () => {
  it('stays silent (null) for a drop on empty canvas', () => {
    expect(connectionFailureReason({ ...base, toNodeId: null })).toBeNull();
    expect(connectionFailureReason({ ...base, toNodeId: undefined })).toBeNull();
  });

  it('stays silent (null) for a valid output → node drop', () => {
    expect(connectionFailureReason(base)).toBeNull();
  });

  it('reports dragging from a non-output handle', () => {
    expect(connectionFailureReason({ ...base, fromHandleType: 'target' })).toBe(
      'Start the connection from an output port.',
    );
    expect(connectionFailureReason({ ...base, fromHandleType: null })).toBe(
      'Start the connection from an output port.',
    );
  });

  it('reports a self-connection', () => {
    expect(connectionFailureReason({ ...base, toNodeId: 'a' })).toBe(
      "A node can't connect to itself.",
    );
  });

  it('reports dropping on a container node', () => {
    expect(connectionFailureReason({ ...base, toNodeIsContainer: true })).toBe(
      'Drop onto a node inside the container, not the container itself.',
    );
  });

  it('reports a target node with no input port', () => {
    expect(connectionFailureReason({ ...base, toNodeHasInput: false })).toBe(
      'That node has no input port to connect to.',
    );
  });

  it('prioritises the output-port rule over self / container / input checks', () => {
    // Dragged from an input AND released on itself: the output-port hint wins.
    expect(
      connectionFailureReason({
        ...base,
        fromHandleType: 'target',
        toNodeId: 'a',
        toNodeIsContainer: true,
        toNodeHasInput: false,
      }),
    ).toBe('Start the connection from an output port.');
  });
});
