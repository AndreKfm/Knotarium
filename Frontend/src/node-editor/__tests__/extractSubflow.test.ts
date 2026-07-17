// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import {
  analyzeExtraction, planExtraction, analyzeMultiExtraction, planParametrizedExtraction,
  partitionRegions, refsOf, writesOf, rootOf, type ExNode, type ExEdge,
} from '../extractSubflow';

let edgeSeq = 0;
const ids = () => ({ callNodeId: 'call-1', startId: 'start-1', endId: 'end-1', newEdgeId: () => `ne-${++edgeSeq}` });

// Helpers to build a small graph.
const node = (id: string, type: string, properties: Record<string, unknown> = {}, triggerOnly = false): ExNode =>
  ({ id, type, properties, triggerOnly });
const edge = (id: string, source: string, target: string): ExEdge =>
  ({ id, source, sourceHandle: 'result', target, targetHandle: 'in' });

describe('rootOf', () => {
  it('takes the first dotted segment (matching compiler scoping)', () => {
    expect(rootOf('signal.params.GlobalContactID')).toBe('signal');
    expect(rootOf('counter')).toBe('counter');
  });
});

describe('refsOf / writesOf', () => {
  it('finds variable + node references nested in properties', () => {
    const n = node('c', 'condition', {
      logic: { cmps: [{ a: '{{ $variables.signal.params.GlobalContactID }}', b: '{{ $node.upstream.result }}' }] },
    });
    const { varRoots, nodeRefs } = refsOf(n);
    expect([...varRoots]).toEqual(['signal']);
    expect([...nodeRefs]).toEqual(['upstream']);
  });

  it('detects writes for setVariable and setVariables', () => {
    expect([...writesOf(node('s', 'setVariable', { variableName: 'total.sum' }))]).toEqual(['total']);
    expect([...writesOf(node('s', 'setVariables', { variables: [{ key: 'a', value: 1 }, { key: 'b', value: 2 }] }))]).toEqual(['a', 'b']);
    expect([...writesOf(node('f', 'fireAction', {}))]).toEqual([]);
  });
});

describe('analyzeExtraction — the screenshot case', () => {
  // trigger -> condition(reads signal.params) -> fireAction ; select condition + fireAction
  const nodes = [
    node('trig', 'eventTrigger', {}, true),
    node('cond', 'condition', { logic: { a: '{{ $variables.signal.params.GlobalContactID }}' } }),
    node('fire', 'fireAction', { action: 'CloseContact' }),
  ];
  const edges = [edge('e1', 'trig', 'cond'), edge('e2', 'cond', 'fire')];

  it('extracts a single-entry / terminal region and passes signal in', () => {
    const a = analyzeExtraction(nodes, edges, ['cond', 'fire']);
    expect(a.ok).toBe(true);
    expect(a.entryNodeId).toBe('cond');
    expect(a.exitNodeId).toBeNull();           // terminal — nothing after fireAction
    expect(a.inputs).toEqual(['signal']);       // signal read inside, produced by the run context outside
    expect(a.outputs).toEqual([]);
    expect(a.entryEdges.map((e) => e.id)).toEqual(['e1']);
    expect(a.internalEdges.map((e) => e.id)).toEqual(['e2']);
  });

  it('refuses to take the trigger into the subflow', () => {
    const a = analyzeExtraction(nodes, edges, ['trig', 'cond']);
    expect(a.ok).toBe(false);
    expect(a.reason).toMatch(/trigger/i);
  });
});

describe('analyzeExtraction — middle region with entry and exit', () => {
  // a -> b -> c -> d ; select b + c
  const nodes = ['a', 'b', 'c', 'd'].map((id) => node(id, 'log'));
  const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c'), edge('e3', 'c', 'd')];

  it('finds single entry b and single exit c', () => {
    const a = analyzeExtraction(nodes, edges, ['b', 'c']);
    expect(a.ok).toBe(true);
    expect(a.entryNodeId).toBe('b');
    expect(a.exitNodeId).toBe('c');
    expect(a.entryEdges.map((e) => e.id)).toEqual(['e1']);
    expect(a.exitEdges.map((e) => e.id)).toEqual(['e3']);
  });
});

describe('analyzeExtraction — rejections', () => {
  it('rejects an empty selection', () => {
    expect(analyzeExtraction([], [], []).reason).toMatch(/at least one/i);
  });

  it('rejects multiple entry points', () => {
    // a -> c, b -> c, c -> d ; select c + d, but two externals enter... use two entries into the region
    const nodes = ['a', 'b', 'c', 'd'].map((id) => node(id, 'log'));
    const edges = [edge('e1', 'a', 'c'), edge('e2', 'b', 'd'), edge('e3', 'c', 'd')];
    const a = analyzeExtraction(nodes, edges, ['c', 'd']);
    expect(a.ok).toBe(false);
    expect(a.reason).toMatch(/more than one entry/i);
  });

  it('allows one exit node that fans out to several external successors', () => {
    // x -> a -> y, a -> z ; select a. One exit NODE (a) with two external successors is preservable —
    // the call node's `result` fans out to both.
    const nodes = ['x', 'a', 'y', 'z'].map((id) => node(id, 'log'));
    const edges = [edge('e1', 'x', 'a'), edge('e2', 'a', 'y'), edge('e3', 'a', 'z')];
    const a = analyzeExtraction(nodes, edges, ['a']);
    expect(a.ok).toBe(true);
    expect(a.exitNodeId).toBe('a');
    expect(a.exitEdges.map((e) => e.id)).toEqual(['e2', 'e3']);
  });

  it('rejects two different selected nodes each leaving to outside', () => {
    // x -> a -> b ; a -> y ; b -> z. select {a,b}: exits leave from both a and b.
    const nodes = ['x', 'a', 'b', 'y', 'z'].map((id) => node(id, 'log'));
    const edges = [edge('e1', 'x', 'a'), edge('e2', 'a', 'b'), edge('e3', 'a', 'y'), edge('e4', 'b', 'z')];
    const a = analyzeExtraction(nodes, edges, ['a', 'b']);
    expect(a.ok).toBe(false);
    expect(a.reason).toMatch(/more than one exit/i);
  });

  it('rejects a disconnected selection', () => {
    const nodes = ['a', 'b', 'c', 'd'].map((id) => node(id, 'log'));
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'c', 'd')];
    const a = analyzeExtraction(nodes, edges, ['b', 'c']);
    expect(a.ok).toBe(false);
    expect(a.reason).toMatch(/connected/i);
  });

  it('rejects a kept node that reads a selected node’s output', () => {
    // sel: b ; kept d reads $node.b.result
    const nodes = [node('a', 'log'), node('b', 'http'), node('d', 'log', { msg: '{{ $node.b.result }}' })];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'b', 'd')];
    const a = analyzeExtraction(nodes, edges, ['b']);
    expect(a.ok).toBe(false);
    expect(a.reason).toMatch(/\$node\.b/);
  });
});

describe('analyzeExtraction — variable outputs', () => {
  it('exports a variable written inside and read by a kept node', () => {
    // x -> set(counter) -> y ; y reads $variables.counter. select set.
    const nodes = [
      node('x', 'log'),
      node('set', 'setVariable', { variableName: 'counter', value: 5 }),
      node('y', 'log', { msg: '{{ $variables.counter }}' }),
    ];
    const edges = [edge('e1', 'x', 'set'), edge('e2', 'set', 'y')];
    const a = analyzeExtraction(nodes, edges, ['set']);
    expect(a.ok).toBe(true);
    expect(a.outputs).toEqual(['counter']);
    expect(a.inputs).toEqual([]); // counter is written inside, not an input
  });

  it('does not export a variable nobody outside reads', () => {
    const nodes = [
      node('x', 'log'),
      node('set', 'setVariable', { variableName: 'tmp', value: 1 }),
      node('use', 'log', { msg: '{{ $variables.tmp }}' }),
    ];
    const edges = [edge('e1', 'x', 'set'), edge('e2', 'set', 'use')];
    const a = analyzeExtraction(nodes, edges, ['set', 'use']); // both inside → tmp stays internal
    expect(a.ok).toBe(true);
    expect(a.outputs).toEqual([]);
    expect(a.inputs).toEqual([]);
  });
});

describe('planExtraction — screenshot case (condition + fireAction under a trigger)', () => {
  const nodes = [
    node('trig', 'eventTrigger', {}, true),
    node('cond', 'condition', { logic: { a: '{{ $variables.signal.params.GlobalContactID }}' } }),
    node('fire', 'fireAction', { action: 'CloseContact' }),
  ];
  const edges = [edge('e1', 'trig', 'cond'), edge('e2', 'cond', 'fire')];

  it('builds child + call node + parent rewire that preserves behavior', () => {
    const a = analyzeExtraction(nodes, edges, ['cond', 'fire']);
    expect(a.ok).toBe(true);
    const plan = planExtraction(nodes, edges, ['cond', 'fire'], a, ids());

    // Child = start -> cond -> fire -> end, with the signal input declared on start.
    expect(plan.child.nodes.map((n) => n.id)).toEqual(['start-1', 'cond', 'fire', 'end-1']);
    expect(plan.interfaceInputs).toEqual([{ name: 'signal', type: 'string' }]);
    expect(plan.interfaceOutputs).toEqual([]);
    const childPairs = plan.child.edges.map((e) => `${e.source}->${e.target}`);
    expect(childPairs).toContain('start-1->cond'); // start feeds the entry
    expect(childPairs).toContain('cond->fire');     // internal edge preserved
    expect(childPairs).toContain('fire->end-1');    // terminal leaf feeds end

    // Call node passes the ambient signal in (the whole point — else the condition breaks inside).
    expect(plan.callProps.subflowInputs).toEqual([{ target: 'signal', value: '{{ $variables.signal }}' }]);
    expect(plan.callProps.subflowOutputs).toEqual([]);

    // Parent: drop cond/fire + their edges; trigger now feeds the call node's `in`.
    expect(plan.nodesToRemove).toEqual(['cond', 'fire']);
    expect(plan.parentEdgesToRemove.sort()).toEqual(['e1', 'e2']);
    expect(plan.parentEdgesToAdd).toHaveLength(1);
    const added = plan.parentEdgesToAdd[0];
    expect(`${added.source}->${added.target}`).toBe('trig->call-1');
    expect(added.targetHandle).toBe('in');
  });

  it('rewires a middle region external successor to the call node result', () => {
    const ns = ['a', 'b', 'c', 'd'].map((id) => node(id, 'log'));
    const es = [edge('e1', 'a', 'b'), edge('e2', 'b', 'c'), edge('e3', 'c', 'd')];
    const a = analyzeExtraction(ns, es, ['b', 'c']);
    const plan = planExtraction(ns, es, ['b', 'c'], a, ids());
    // a -> call -> d
    const pairs = plan.parentEdgesToAdd.map((e) => `${e.source}->${e.target}`);
    expect(pairs).toContain('a->call-1');
    expect(pairs).toContain('call-1->d');
  });
});

describe('Stage 3 — parametrized extraction of N isomorphic chains', () => {
  // Two chains, each trigger -> condition(reads signal; differs only in the compared contact id) -> fire.
  // Uses the REAL Condition operand shape ({kind:'lit', type, value}) so the operand->ref fix is covered.
  const cond = (id: string, contactId: number) =>
    node(id, 'condition', { logic: { version: 1, comb: 'and', cmps: [
      { id: 'c1', op: 'eq', a: { kind: 'ref', ref: '{{ signal.params.GlobalContactID }}', type: 'number' }, b: { kind: 'lit', type: 'number', value: contactId } },
    ] } });
  const nodes = [
    node('trigA', 'eventTrigger', {}, true), cond('condA', 1043), node('fireA', 'fireAction', { action: 'CloseContact' }),
    node('trigB', 'eventTrigger', {}, true), cond('condB', 1044), node('fireB', 'fireAction', { action: 'CloseContact' }),
  ];
  const edges = [
    edge('a1', 'trigA', 'condA'), edge('a2', 'condA', 'fireA'),
    edge('b1', 'trigB', 'condB'), edge('b2', 'condB', 'fireB'),
  ];
  const selected = ['condA', 'fireA', 'condB', 'fireB'];

  it('partitions the selection into the two chains', () => {
    const regions = partitionRegions(selected, edges);
    expect(regions).toHaveLength(2);
    expect(new Set(regions.flat())).toEqual(new Set(selected));
  });

  it('finds one parameter (the differing contact id) named from the compared field', () => {
    const m = analyzeMultiExtraction(nodes, edges, selected);
    expect(m.ok).toBe(true);
    expect(m.signature).toEqual(['condition', 'fireAction']);
    expect(m.params).toHaveLength(1);
    const p = m.params[0];
    expect(p.orderIndex).toBe(0);                 // the condition node
    expect(p.path).toEqual(['logic', 'cmps', 0, 'b', 'value']);
    expect(p.valuesByRegion).toEqual([1043, 1044]);
    expect(p.name).toBe('globalContactID');       // derived from the comparator's `a` ref
  });

  it('builds one parametrized subflow + one call node per chain (operand → ref)', () => {
    const m = analyzeMultiExtraction(nodes, edges, selected);
    let seq = 0;
    const gen = (t: string) => `${t}-${++seq}`;
    let e = 0;
    const plan = planParametrizedExtraction(nodes, m, gen, () => `pe-${++e}`);

    // Canonical child: start -> condition -> fireAction -> end, with the literal operand swapped to a ref
    // (NOT a `{{…}}` string in `value`, which would make the condition incomplete).
    expect(plan.child.nodes.map((n) => n.type)).toEqual(['start', 'condition', 'fireAction', 'end']);
    const childCond = plan.child.nodes.find((n) => n.type === 'condition')!;
    const operand = (childCond.properties as any).logic.cmps[0].b;
    expect(operand).toEqual({ kind: 'ref', ref: `{{ $variables.${plan.params[0].name} }}`, type: 'number' });
    // FireAction's static prop is identical across chains → stays literal.
    expect((plan.child.nodes.find((n) => n.type === 'fireAction')!.properties as any).action).toBe('CloseContact');

    expect(plan.interfaceInputs.map((v) => v.name)).toEqual(['globalContactID']);

    // Two call nodes, each binding its own contact id (typed number, not a string).
    expect(plan.calls).toHaveLength(2);
    const valOf = (ci: number, name: string) => plan.calls[ci].subflowInputs.find((b) => b.target === name)!.value;
    expect(valOf(0, plan.params[0].name)).toBe(1043);
    expect(valOf(1, plan.params[0].name)).toBe(1044);

    // Each call removes its chain and rewires its trigger -> call.
    expect(new Set(plan.nodesToRemove)).toEqual(new Set(selected));
    const aCall = plan.calls[0];
    expect(aCall.edgesToAdd.some((x) => x.source === 'trigA' && x.target === aCall.callNodeId)).toBe(true);
  });
});
