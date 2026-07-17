// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import { comparatorLeaves, presetSamples, sampleForLeaf } from './conditionTestPresets';
import type { DraftTree } from './conditionTree';

const GID = '{{ signal.params.GlobalContactId }}';
const CHG = '{{ signal.params.ChangedTo }}';
// A: GlobalContactId (number) = 1045 ; B: ChangedTo (boolean) = true
const tree: DraftTree = {
  root: {
    kind: 'group', id: 'g1', op: 'and', children: [
      { kind: 'cmp', id: 'c1', op: 'eq', a: { kind: 'ref', type: 'number', ref: GID }, b: { kind: 'lit', type: 'number', text: '1045' } },
      { kind: 'cmp', id: 'c2', op: 'eq', a: { kind: 'ref', type: 'boolean', ref: CHG }, b: { kind: 'lit', type: 'boolean', text: 'true' } },
    ],
  },
};

describe('conditionTestPresets', () => {
  it('extracts ref leaves with their comparand + op (in tree order)', () => {
    const leaves = comparatorLeaves(tree);
    expect(leaves.map((l) => l.ref)).toEqual([GID, CHG]);
    expect(leaves[0]).toMatchObject({ op: 'eq', comparand: '1045', refType: 'number' });
  });

  it('allPass drives each ref to satisfy its comparator', () => {
    const s = presetSamples(tree, { kind: 'allPass' });
    expect(s[GID]).toBe('1045');
    expect(s[CHG]).toBe('true');
  });

  it('allFail drives each ref away from its comparator', () => {
    const s = presetSamples(tree, { kind: 'allFail' });
    expect(s[GID]).not.toBe('1045');
    expect(s[CHG]).toBe('false');
  });

  it('failOne fails only the named comparator and passes the rest', () => {
    const s = presetSamples(tree, { kind: 'failOne', id: 'c1' });
    expect(s[GID]).not.toBe('1045'); // c1 fails
    expect(s[CHG]).toBe('true');     // c2 passes
  });

  it('derives pass/fail for ordering operators around the threshold', () => {
    const gt = { id: 'x', op: 'gt', ref: 'r', refType: 'number' as const, comparand: '10' };
    expect(Number(sampleForLeaf(gt, 'pass'))).toBeGreaterThan(10);
    expect(Number(sampleForLeaf(gt, 'fail'))).toBeLessThanOrEqual(10);
  });
});
