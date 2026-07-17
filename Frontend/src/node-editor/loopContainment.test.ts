// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { inferLoopContainment, isLoopContainerType, orderParentsBeforeChildren } from './loopContainment';

const n = (id: string, type?: string) => ({ id, type });
const e = (source: string, target: string, sourceHandle?: string) => ({ source, target, sourceHandle });

describe('isLoopContainerType', () => {
  it('recognises the container node types', () => {
    expect(isLoopContainerType('forLoop')).toBe(true);
    expect(isLoopContainerType('parallelForEach')).toBe(true);
    expect(isLoopContainerType('fireAction')).toBe(false);
    expect(isLoopContainerType(undefined)).toBe(false);
  });
});

describe('inferLoopContainment', () => {
  it('claims the body reached from the loop start that loops back, excluding pre/post nodes', () => {
    const nodes = [n('start', 'start'), n('loop', 'forLoop'), n('a', 'fireAction'), n('b', 'setVariable'), n('end', 'end')];
    const edges = [
      e('start', 'loop', 'result'),
      e('loop', 'a', 'start'),   // body entry
      e('a', 'b'),
      e('b', 'loop'),            // loop-back into the container
      e('loop', 'end', 'done'),  // post-loop continuation
    ];
    const map = inferLoopContainment(nodes, edges);
    expect(map.get('a')).toBe('loop');
    expect(map.get('b')).toBe('loop');
    // Nodes before/after the loop stay top-level.
    expect(map.has('start')).toBe(false);
    expect(map.has('end')).toBe(false);
    expect(map.has('loop')).toBe(false);
  });

  it('does not claim a node reached only from the loop\'s post-loop (done) output', () => {
    const nodes = [n('loop', 'forLoop'), n('body', 'fireAction'), n('after', 'setVariable')];
    const edges = [
      e('loop', 'body', 'start'),
      e('body', 'loop'),
      e('loop', 'after', 'done'),
    ];
    const map = inferLoopContainment(nodes, edges);
    expect(map.get('body')).toBe('loop');
    expect(map.has('after')).toBe(false);
  });

  it('assigns nested-loop bodies to the innermost container', () => {
    const nodes = [n('outer', 'forLoop'), n('inner', 'forLoop'), n('deep', 'fireAction')];
    const edges = [
      e('outer', 'inner', 'start'), // inner loop is the outer loop's body
      e('inner', 'outer'),          // inner loops back to outer
      e('inner', 'deep', 'start'),  // deep is the inner loop's body
      e('deep', 'inner'),           // deep loops back to inner
    ];
    const map = inferLoopContainment(nodes, edges);
    expect(map.get('inner')).toBe('outer'); // the inner container nests in the outer
    expect(map.get('deep')).toBe('inner');  // its node goes to the innermost, not the outer
  });

  it('returns an empty map when there are no containers', () => {
    const map = inferLoopContainment([n('a', 'fireAction'), n('b', 'setVariable')], [e('a', 'b')]);
    expect(map.size).toBe(0);
  });
});

describe('orderParentsBeforeChildren', () => {
  it('moves a parent ahead of a child that precedes it', () => {
    const ordered = orderParentsBeforeChildren([
      { id: 'child', parentId: 'loop' },
      { id: 'loop' },
      { id: 'top' },
    ]);
    const ids = ordered.map((n) => n.id);
    expect(ids.indexOf('loop')).toBeLessThan(ids.indexOf('child'));
    expect(ids).toContain('top');
    expect(ordered).toHaveLength(3);
  });

  it('is stable and tolerates a dangling parentId', () => {
    const ordered = orderParentsBeforeChildren([
      { id: 'a' },
      { id: 'b', parentId: 'missing' },
    ]);
    expect(ordered.map((n) => n.id)).toEqual(['a', 'b']);
  });
});
