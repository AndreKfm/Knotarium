// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import { buildConditionTreeFlow, type ConditionTreeFlow, type ComparatorNodeData, type GroupNodeData, type OutputNodeData } from './conditionFlowTree';
import type { PreviewValueProvider } from './conditionPreview';
import type { DraftCmpNode, DraftTree } from './conditionTree';

const noValues: PreviewValueProvider = () => ({ found: false });
const cmp = (id: string, a: string, op: string, b: string): DraftCmpNode => ({
  kind: 'cmp',
  id,
  op,
  a: { kind: 'lit', type: 'number', text: a },
  b: { kind: 'lit', type: 'number', text: b },
});

const ids = (flow: ConditionTreeFlow) => flow.nodes.map((n) => n.id).sort();
const edge = (flow: ConditionTreeFlow, source: string, target: string) =>
  flow.edges.find((e) => e.source === source && e.target === target);

describe('buildConditionTreeFlow', () => {
  it('renders the empty-first placeholder wired to the output', () => {
    const flow = buildConditionTreeFlow({ root: null }, noValues);
    expect(ids(flow)).toEqual(['out', 'placeholder']);
    expect(edge(flow, 'placeholder', 'out')).toBeTruthy();
  });

  it('wires a single comparator: inputs → cmp → output', () => {
    const flow = buildConditionTreeFlow({ root: cmp('c1', '5', 'eq', '5') }, noValues);
    expect(ids(flow)).toEqual(['cmp:c1', 'in:c1:a', 'in:c1:b', 'out']);
    expect(edge(flow, 'in:c1:a', 'cmp:c1')!.wire).toBe('value');
    expect(edge(flow, 'cmp:c1', 'out')!.wire).toBe('boolean');
  });

  it('drops the B input for a unary operator', () => {
    const unary: DraftCmpNode = { kind: 'cmp', id: 'c1', op: 'exists', a: { kind: 'ref', type: 'string', ref: '{{ x }}' }, b: null };
    const flow = buildConditionTreeFlow({ root: unary }, noValues);
    expect(flow.nodes.find((n) => n.id === 'in:c1:b')).toBeUndefined();
    expect(flow.nodes.find((n) => n.id === 'in:c1:a')).toBeTruthy();
  });

  it('builds a nested tree and wires children into their group/NOT, group into the output', () => {
    const tree: DraftTree = {
      root: { kind: 'group', id: 'g1', op: 'and', children: [cmp('c1', '5', 'eq', '5'), { kind: 'not', id: 'n1', child: cmp('c2', '1', 'eq', '2') }] },
    };
    const flow = buildConditionTreeFlow(tree, noValues);
    expect(edge(flow, 'group:g1', 'out')).toBeTruthy();
    expect(edge(flow, 'cmp:c1', 'group:g1')).toBeTruthy();
    expect(edge(flow, 'not:n1', 'group:g1')).toBeTruthy();
    expect(edge(flow, 'cmp:c2', 'not:n1')).toBeTruthy();
  });

  it('colors each boolean wire by its source node status', () => {
    // c1 (5=5 True) AND NOT(c2 1=2 False → True) → group True.
    const tree: DraftTree = {
      root: { kind: 'group', id: 'g1', op: 'and', children: [cmp('c1', '5', 'eq', '5'), { kind: 'not', id: 'n1', child: cmp('c2', '1', 'eq', '2') }] },
    };
    const flow = buildConditionTreeFlow(tree, noValues);
    expect(edge(flow, 'cmp:c1', 'group:g1')!.status).toBe('true');
    expect(edge(flow, 'cmp:c2', 'not:n1')!.status).toBe('false');
    expect(edge(flow, 'not:n1', 'group:g1')!.status).toBe('true'); // NOT False
    expect(edge(flow, 'group:g1', 'out')!.status).toBe('true');
  });

  it('pins operand A above operand B so the layout reads "A op B" top→bottom', () => {
    const flow = buildConditionTreeFlow({ root: cmp('c1', '2', 'gt', '3') }, noValues);
    const a = flow.nodes.find((n) => n.id === 'in:c1:a')!;
    const b = flow.nodes.find((n) => n.id === 'in:c1:b')!;
    expect(a.y).toBeLessThan(b.y);
  });

  it('aligns the output vertically with its feeder so the final wire is straight', () => {
    const tree: DraftTree = {
      root: { kind: 'group', id: 'g1', op: 'and', children: [cmp('c1', '5', 'eq', '5'), cmp('c2', '1', 'eq', '2')] },
    };
    const flow = buildConditionTreeFlow(tree, noValues);
    const out = flow.nodes.find((n) => n.id === 'out')!;
    const group = flow.nodes.find((n) => n.id === 'group:g1')!;
    expect(out.y + out.height / 2).toBeCloseTo(group.y + group.height / 2, 5);
  });

  it('flags the B input of a list-right op as a list and labels it as a set', () => {
    const inOp: DraftCmpNode = {
      kind: 'cmp',
      id: 'c1',
      op: 'in',
      a: { kind: 'lit', type: 'number', text: '3' },
      b: { kind: 'lit', type: 'string', text: '0, 2, 3' },
    };
    const flow = buildConditionTreeFlow({ root: inOp }, noValues);
    const a = flow.nodes.find((n) => n.id === 'in:c1:a')!.data as { isList: boolean };
    const b = flow.nodes.find((n) => n.id === 'in:c1:b')!.data as { isList: boolean; label: string };
    expect(a.isList).toBe(false);
    expect(b.isList).toBe(true);
    expect(b.label).toBe('{0, 2, 3}');
  });

  it('clamps a long value-wire label so it does not sprawl across the canvas', () => {
    const longList: DraftCmpNode = {
      kind: 'cmp',
      id: 'c1',
      op: 'in',
      a: { kind: 'lit', type: 'number', text: '5' },
      b: { kind: 'lit', type: 'string', text: '4, 1, 2, 0, 3, 4, 5, 6, 6, 7, 7, 8, 8, 9, 6' },
    };
    const flow = buildConditionTreeFlow({ root: longList }, noValues);
    const label = edge(flow, 'in:c1:b', 'cmp:c1')!.label!;
    expect(label.length).toBeLessThanOrEqual(18);
    expect(label.endsWith('…')).toBe(true);
  });

  it('assigns distinct left→right positions via dagre', () => {
    const flow = buildConditionTreeFlow({ root: cmp('c1', '5', 'eq', '5') }, noValues);
    const input = flow.nodes.find((n) => n.id === 'in:c1:a')!;
    const out = flow.nodes.find((n) => n.id === 'out')!;
    expect(out.x).toBeGreaterThan(input.x); // output is downstream (to the right)
  });

  // ── awaiting (runtime) vs genuinely-incomplete presentation ──
  // A comparator whose ref operand has no design-time value (the imported `signal.params.*` case) is
  // valid but not previewable — it must read "runtime", not "incomplete", on the wire/chip/output.
  const refEq = (id: string): DraftCmpNode => ({
    kind: 'cmp', id, op: 'eq',
    a: { kind: 'ref', type: 'boolean', ref: '{{ signal.params.ChangedTo }}' },
    b: { kind: 'lit', type: 'boolean', text: 'true' },
  });

  it('marks a configured-ref comparator with no sample as awaiting/runtime, not incomplete', () => {
    const flow = buildConditionTreeFlow({ root: refEq('c1') }, noValues);
    const boolWire = edge(flow, 'cmp:c1', 'out')!;
    expect(boolWire.status).toBe('awaiting');
    expect(boolWire.label).toBe('runtime');
    expect((flow.nodes.find((n) => n.id === 'cmp:c1')!.data as ComparatorNodeData).awaiting).toBe(true);
    expect((flow.nodes.find((n) => n.id === 'out')!.data as OutputNodeData).awaiting).toBe(true);
  });

  it('propagates awaiting up an AND group when every incomplete child is a runtime ref', () => {
    const tree: DraftTree = { root: { kind: 'group', id: 'g1', op: 'and', children: [refEq('c1'), refEq('c2')] } };
    const flow = buildConditionTreeFlow(tree, noValues);
    expect((flow.nodes.find((n) => n.id === 'group:g1')!.data as GroupNodeData).awaiting).toBe(true);
    expect(edge(flow, 'group:g1', 'out')!.status).toBe('awaiting');
  });

  it('keeps a genuinely-empty operand as incomplete (not awaiting)', () => {
    const emptyLit: DraftCmpNode = {
      kind: 'cmp', id: 'c1', op: 'eq',
      a: { kind: 'lit', type: 'number', text: '' }, // author hasn't filled it in
      b: { kind: 'lit', type: 'number', text: '5' },
    };
    const flow = buildConditionTreeFlow({ root: emptyLit }, noValues);
    const boolWire = edge(flow, 'cmp:c1', 'out')!;
    expect(boolWire.status).toBe('incomplete');
    expect(boolWire.label).toBe('incomplete');
    expect((flow.nodes.find((n) => n.id === 'cmp:c1')!.data as ComparatorNodeData).awaiting).toBe(false);
  });

  it('centers the combiner (and output) on the vertical midpoint of the comparators feeding it', () => {
    const tree: DraftTree = { root: { kind: 'group', id: 'g1', op: 'and', children: [cmp('c1', '5', 'eq', '5'), cmp('c2', '1', 'eq', '2')] } };
    const flow = buildConditionTreeFlow(tree, noValues);
    const center = (id: string) => { const n = flow.nodes.find((x) => x.id === id)!; return n.y + n.height / 2; };
    const kidsMid = (center('cmp:c1') + center('cmp:c2')) / 2;
    expect(Math.abs(center('group:g1') - kidsMid)).toBeLessThan(0.5); // group centered on its children
    expect(Math.abs(center('out') - center('group:g1'))).toBeLessThan(0.5); // output centered on the group
  });

  it('lays sibling comparators out top→bottom in AUTHORED order (dagre must not relabel them)', () => {
    // Three comparators under one group; the editor labels them A/B/C by visual top-to-bottom order, so
    // the rendered order MUST follow the order they were authored (c1, c2, c3) regardless of dagre's
    // same-rank heuristic — otherwise the summary letters point at the wrong rows.
    const tree: DraftTree = {
      root: { kind: 'group', id: 'g1', op: 'or', children: [cmp('c1', '5', 'eq', '5'), cmp('c2', '1', 'eq', '2'), cmp('c3', '7', 'eq', '7')] },
    };
    const flow = buildConditionTreeFlow(tree, noValues);
    const y = (id: string) => flow.nodes.find((n) => n.id === id)!.y;
    expect(y('cmp:c1')).toBeLessThan(y('cmp:c2'));
    expect(y('cmp:c2')).toBeLessThan(y('cmp:c3'));
    // input cards follow their comparator
    expect(y('in:c1:a')).toBeLessThan(y('in:c2:a'));
    expect(y('in:c2:a')).toBeLessThan(y('in:c3:a'));
  });

  it('centers combiners on their children bottom-up, including a nested group', () => {
    const tree: DraftTree = {
      root: {
        kind: 'group', id: 'g1', op: 'and', children: [
          cmp('c1', '5', 'eq', '5'),
          { kind: 'group', id: 'g2', op: 'or', children: [cmp('c2', '1', 'eq', '2'), cmp('c3', '3', 'eq', '3')] },
        ],
      },
    };
    const flow = buildConditionTreeFlow(tree, noValues);
    const center = (id: string) => { const n = flow.nodes.find((x) => x.id === id)!; return n.y + n.height / 2; };
    // inner group centered on its two comparators
    expect(Math.abs(center('group:g2') - (center('cmp:c2') + center('cmp:c3')) / 2)).toBeLessThan(0.5);
    // root centered on [c1, g2]; output centered on root
    expect(Math.abs(center('group:g1') - (center('cmp:c1') + center('group:g2')) / 2)).toBeLessThan(0.5);
    expect(Math.abs(center('out') - center('group:g1'))).toBeLessThan(0.5);
  });

  it('does NOT mark a group awaiting when it mixes a runtime ref with a genuinely-empty operand', () => {
    const emptyLit: DraftCmpNode = {
      kind: 'cmp', id: 'c2', op: 'eq',
      a: { kind: 'lit', type: 'number', text: '' }, b: { kind: 'lit', type: 'number', text: '5' },
    };
    const tree: DraftTree = { root: { kind: 'group', id: 'g1', op: 'and', children: [refEq('c1'), emptyLit] } };
    const flow = buildConditionTreeFlow(tree, noValues);
    expect((flow.nodes.find((n) => n.id === 'group:g1')!.data as GroupNodeData).awaiting).toBe(false);
    expect(edge(flow, 'group:g1', 'out')!.status).toBe('incomplete');
  });
});
