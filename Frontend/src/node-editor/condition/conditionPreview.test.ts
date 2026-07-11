import { describe, expect, it } from 'vitest';
import { evaluateDraft, evaluateDraftTree, evaluateDraftTreeNodes, toResolvedCondition, type PreviewValueProvider } from './conditionPreview';
import type { DraftCondition } from './conditionModel';
import type { DraftTree } from './conditionTree';

const lit = (type: 'string' | 'number' | 'boolean', text: string) => ({ kind: 'lit' as const, type, text });
const ref = (type: 'string' | 'number' | 'boolean', r: string) => ({ kind: 'ref' as const, type, ref: r });

const noValues: PreviewValueProvider = () => ({ found: false });

describe('toResolvedCondition', () => {
  it('maps parseable literals to typed raw values', () => {
    const draft: DraftCondition = { comb: 'and', cmps: [{ id: 'c1', op: 'eq', a: lit('number', '5'), b: lit('number', '5') }] };
    const resolved = toResolvedCondition(draft, noValues);
    expect(resolved.comparators[0].a).toEqual({ type: 'number', state: 'value', raw: 5 });
  });

  it('hands an unparseable number literal through as text so coercion surfaces the error', () => {
    const draft: DraftCondition = { comb: 'and', cmps: [{ id: 'c1', op: 'eq', a: lit('number', 'abc'), b: lit('number', '1') }] };
    const a = toResolvedCondition(draft, noValues).comparators[0].a;
    expect(a).toEqual({ type: 'number', state: 'value', raw: 'abc' });
    // and the evaluator turns that into a COERCION_FAILED error
    expect(evaluateDraft(draft, noValues).status).toBe('Error');
  });

  it('treats an empty literal and an empty ref as unset (Incomplete), not error', () => {
    const draft: DraftCondition = {
      comb: 'and',
      cmps: [
        { id: 'c1', op: 'eq', a: lit('string', ''), b: lit('string', 'x') },
        { id: 'c2', op: 'eq', a: ref('string', ''), b: lit('string', 'x') },
      ],
    };
    const resolved = toResolvedCondition(draft, noValues);
    expect(resolved.comparators[0].a.state).toBe('unset');
    expect(resolved.comparators[1].a.state).toBe('unset');
    expect(evaluateDraft(draft, noValues).status).toBe('Incomplete');
  });

  it('a benign (manual) miss is Incomplete in preview, never RESOLUTION_FAILED', () => {
    const draft: DraftCondition = { comb: 'and', cmps: [{ id: 'c1', op: 'eq', a: ref('string', '{{ x }}'), b: lit('string', 'x') }] };
    const outcome = evaluateDraft(draft, noValues);
    expect(outcome.status).toBe('Incomplete');
  });

  it('an AUTHORITATIVE miss (last run / dry run) previews as RESOLUTION_FAILED — what-you-see==what-runs', () => {
    // The author looking at a last-run preview must see the runtime fail-node, not a soft "incomplete".
    const authoritative: PreviewValueProvider = () => ({ found: false, authoritativeMiss: true });
    const draft: DraftCondition = { comb: 'and', cmps: [{ id: 'c1', op: 'eq', a: ref('string', '{{ x }}'), b: lit('string', 'x') }] };
    const resolved = toResolvedCondition(draft, authoritative);
    expect(resolved.comparators[0].a.state).toBe('unresolved');
    const outcome = evaluateDraft(draft, authoritative);
    expect(outcome.status).toBe('Error');
    expect(outcome.error?.code).toBe('RESOLUTION_FAILED');
  });

  it('resolves references via the provider (including a legitimate null)', () => {
    const provider: PreviewValueProvider = (r) =>
      r === '{{ count }}' ? { found: true, value: 7 } : r === '{{ missing }}' ? { found: true, value: null } : { found: false };
    const draft: DraftCondition = {
      comb: 'and',
      cmps: [
        { id: 'c1', op: 'gt', a: ref('number', '{{ count }}'), b: lit('number', '3') },
        { id: 'c2', op: 'nexists', a: ref('string', '{{ missing }}'), b: null },
      ],
    };
    const resolved = toResolvedCondition(draft, provider);
    expect(resolved.comparators[0].a).toEqual({ type: 'number', state: 'value', raw: 7 });
    expect(resolved.comparators[1].a).toEqual({ type: 'string', state: 'value', raw: null });
    expect(evaluateDraft(draft, provider).status).toBe('True'); // 7 > 3 AND missing is null
  });

  it('drops B for a unary operator', () => {
    const draft: DraftCondition = { comb: 'and', cmps: [{ id: 'c1', op: 'exists', a: lit('string', 'x'), b: lit('string', 'ignored') }] };
    expect(toResolvedCondition(draft, noValues).comparators[0].b).toBeNull();
  });
});

describe('evaluateDraftTree (v2 preview)', () => {
  const cmp = (id: string, a: string, op: string, b: string) => ({
    kind: 'cmp' as const,
    id,
    op,
    a: lit('number', a),
    b: lit('number', b),
  });

  it('an empty tree previews as Incomplete', () => {
    expect(evaluateDraftTree({ root: null }, noValues).status).toBe('Incomplete');
  });

  it('folds a nested tree live: A AND (B OR NOT C)', () => {
    // (1=1) AND ( (1=2 → False) OR NOT(1=2 → False) = NOT False = True ) = True.
    const tree: DraftTree = {
      root: {
        kind: 'group',
        id: 'g1',
        op: 'and',
        children: [cmp('c1', '1', 'eq', '1'), { kind: 'group', id: 'g2', op: 'or', children: [cmp('c2', '1', 'eq', '2'), { kind: 'not', id: 'n1', child: cmp('c3', '1', 'eq', '2') }] }],
      },
    };
    expect(evaluateDraftTree(tree, noValues).status).toBe('True');
  });

  it('an authoritative miss in a deep leaf previews as RESOLUTION_FAILED', () => {
    const authoritative: PreviewValueProvider = () => ({ found: false, authoritativeMiss: true });
    const tree: DraftTree = {
      root: { kind: 'not', id: 'n1', child: { kind: 'cmp', id: 'deep', op: 'eq', a: ref('number', '{{ x }}'), b: lit('number', '5') } },
    };
    const outcome = evaluateDraftTree(tree, authoritative);
    expect(outcome.status).toBe('Error');
    expect(outcome.error?.code).toBe('RESOLUTION_FAILED');
  });

  it('evaluateDraftTreeNodes returns every node id status, consistent with the root outcome', () => {
    const tree: DraftTree = {
      root: {
        kind: 'group',
        id: 'g1',
        op: 'and',
        children: [cmp('c1', '1', 'eq', '1'), { kind: 'not', id: 'n1', child: cmp('c2', '1', 'eq', '2') }],
      },
    };
    const { outcome, status } = evaluateDraftTreeNodes(tree, noValues);
    expect(outcome.status).toBe(evaluateDraftTree(tree, noValues).status); // no drift vs evaluateTree
    expect(status.c1).toBe('True'); // 1 = 1
    expect(status.c2).toBe('False'); // 1 = 2
    expect(status.n1).toBe('True'); // NOT False
    expect(status.g1).toBe('True'); // True AND True
  });
});
