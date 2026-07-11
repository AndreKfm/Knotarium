import { describe, expect, it } from 'vitest';
import { summarizeConditionLine, summarizeConditionLines, summarizeLogic, summarizeTree } from './conditionSummary';
import type { ConditionLogic } from './conditionModel';
import type { ConditionLogicTree, PersistedCmpNode, PersistedNode } from './conditionTree';

describe('summarizeLogic', () => {
  it('formats binary comparators with operator symbol/label and quoted strings', () => {
    const logic: ConditionLogic = {
      version: 1,
      comb: 'or',
      cmps: [
        { id: 'c1', op: 'eq', a: { kind: 'ref', type: 'string', ref: '{{ $variables.plan }}' }, b: { kind: 'lit', type: 'string', value: 'pro' } },
        { id: 'c2', op: 'gt', a: { kind: 'lit', type: 'number', value: 5 }, b: { kind: 'lit', type: 'number', value: 1 } },
      ],
    };
    const summary = summarizeLogic(logic);
    expect(summary.comb).toBe('or');
    expect(summary.rows[0]).toEqual({ id: 'c1', symbol: '=', opLabel: 'Equals', a: 'plan', b: '"pro"' });
    expect(summary.rows[1]).toEqual({ id: 'c2', symbol: '>', opLabel: 'Greater than', a: '5', b: '1' });
  });

  it('renders a list-right op B operand as a set, not a quoted string', () => {
    const logic: ConditionLogic = {
      version: 1,
      comb: 'and',
      cmps: [{ id: 'c1', op: 'in', a: { kind: 'ref', type: 'number', ref: '{{ $variables.counter }}' }, b: { kind: 'lit', type: 'string', value: '4, 2, 3, 0' } }],
    };
    const [row] = summarizeLogic(logic).rows;
    expect(row).toEqual({ id: 'c1', symbol: '∈', opLabel: 'Is one of', a: 'counter', b: '{4, 2, 3, 0}' });
  });

  it('renders a unary operator with no B operand', () => {
    const logic: ConditionLogic = {
      version: 1,
      comb: 'and',
      cmps: [{ id: 'c1', op: 'empty', a: { kind: 'ref', type: 'string', ref: '{{ $node.http.output.body }}' } }],
    };
    const [row] = summarizeLogic(logic).rows;
    expect(row).toEqual({ id: 'c1', symbol: '∅', opLabel: 'Is empty', a: 'body', b: null });
  });

  it('falls back gracefully for an unknown operator id', () => {
    const logic: ConditionLogic = {
      version: 1,
      comb: 'and',
      cmps: [{ id: 'c1', op: 'mystery', a: { kind: 'lit', type: 'boolean', value: true } }],
    };
    const [row] = summarizeLogic(logic).rows;
    expect(row.symbol).toBe('?');
    expect(row.opLabel).toBe('mystery');
    expect(row.a).toBe('true');
    expect(row.b).toBeNull();
  });
});

describe('summarizeTree (v2)', () => {
  const num = (v: number) => ({ kind: 'lit' as const, type: 'number' as const, value: v });
  const leaf = (id: string, a: number, op: string, b: number): PersistedCmpNode => ({ kind: 'cmp', id, op, a: num(a), b: num(b) });

  it('renders a bare comparator root', () => {
    expect(summarizeTree(leaf('c1', 5, 'gt', 1))).toBe('5 > 1');
  });

  it('joins a group with its combinator (no outer parens at the root)', () => {
    const root: PersistedNode = { kind: 'group', id: 'g', op: 'and', children: [leaf('c1', 5, 'gt', 1), leaf('c2', 5, 'eq', 5)] };
    expect(summarizeTree(root)).toBe('5 > 1 AND 5 = 5');
  });

  it('parenthesizes nested groups and prefixes NOT — A AND (B OR NOT C)', () => {
    const root: PersistedNode = {
      kind: 'group',
      id: 'g1',
      op: 'and',
      children: [
        leaf('c1', 1, 'eq', 1),
        { kind: 'group', id: 'g2', op: 'or', children: [leaf('c2', 2, 'eq', 2), { kind: 'not', id: 'n1', child: leaf('c3', 3, 'eq', 3) }] },
      ],
    };
    expect(summarizeTree(root)).toBe('1 = 1 AND (2 = 2 OR NOT 3 = 3)');
  });
});

describe('summarizeConditionLine (canvas subtitle)', () => {
  it('summarizes v2 logic as a one-line expression', () => {
    const v2: ConditionLogicTree = {
      version: 2,
      root: { kind: 'cmp', id: 'c1', op: 'gt', a: { kind: 'ref', type: 'number', ref: '{{ $variables.counter }}' }, b: { kind: 'lit', type: 'number', value: 4 } },
    };
    expect(summarizeConditionLine(v2)).toBe('counter > 4');
  });

  it('summarizes v1 logic with its combinator', () => {
    const v1: ConditionLogic = {
      version: 1,
      comb: 'and',
      cmps: [
        { id: 'c1', op: 'gt', a: { kind: 'lit', type: 'number', value: 5 }, b: { kind: 'lit', type: 'number', value: 1 } },
        { id: 'c2', op: 'eq', a: { kind: 'lit', type: 'number', value: 2 }, b: { kind: 'lit', type: 'number', value: 2 } },
      ],
    };
    expect(summarizeConditionLine(v1)).toBe('5 > 1 AND 2 = 2');
  });

  it('parses a stringified-JSON logic blob', () => {
    const raw = JSON.stringify({ version: 2, root: { kind: 'cmp', id: 'c1', op: 'eq', a: { kind: 'lit', type: 'string', value: 'x' }, b: { kind: 'lit', type: 'string', value: 'x' } } });
    expect(summarizeConditionLine(raw)).toBe('"x" = "x"');
  });

  it('summarizes a list (Is one of) membership as a set on the canvas line', () => {
    const v2: ConditionLogicTree = {
      version: 2,
      root: { kind: 'cmp', id: 'c1', op: 'in', a: { kind: 'ref', type: 'number', ref: '{{ $variables.counter }}' }, b: { kind: 'lit', type: 'string', value: '4, 2, 3, 0' } },
    };
    expect(summarizeConditionLine(v2)).toBe('counter ∈ {4, 2, 3, 0}');
  });

  it('returns null for missing / invalid logic (caller falls back)', () => {
    expect(summarizeConditionLine(undefined)).toBeNull();
    expect(summarizeConditionLine('   ')).toBeNull();
    expect(summarizeConditionLine({ foo: 'bar' })).toBeNull();
  });
});

describe('summarizeConditionLines (canvas — break at AND/OR)', () => {
  it('splits a v1 condition into one line per comparator, connector-prefixed', () => {
    const v1: ConditionLogic = {
      version: 1,
      comb: 'and',
      cmps: [
        { id: 'c1', op: 'in', a: { kind: 'ref', type: 'number', ref: '{{ $variables.counter }}' }, b: { kind: 'lit', type: 'string', value: '4, 1, 2, 0' } },
        { id: 'c2', op: 'eq', a: { kind: 'lit', type: 'number', value: 3 }, b: { kind: 'lit', type: 'number', value: 5 } },
      ],
    };
    expect(summarizeConditionLines(v1)).toEqual(['counter ∈ {4, 1, 2, 0}', 'AND 3 = 5']);
  });

  it('splits a v2 top-level group, keeping nested groups inline (parenthesized)', () => {
    const v2: ConditionLogicTree = {
      version: 2,
      root: {
        kind: 'group',
        id: 'g1',
        op: 'and',
        children: [
          { kind: 'cmp', id: 'c1', op: 'eq', a: { kind: 'lit', type: 'number', value: 1 }, b: { kind: 'lit', type: 'number', value: 1 } },
          { kind: 'group', id: 'g2', op: 'or', children: [
            { kind: 'cmp', id: 'c2', op: 'eq', a: { kind: 'lit', type: 'number', value: 2 }, b: { kind: 'lit', type: 'number', value: 2 } },
            { kind: 'not', id: 'n1', child: { kind: 'cmp', id: 'c3', op: 'eq', a: { kind: 'lit', type: 'number', value: 3 }, b: { kind: 'lit', type: 'number', value: 3 } } },
          ] },
        ],
      },
    };
    expect(summarizeConditionLines(v2)).toEqual(['1 = 1', 'AND (2 = 2 OR NOT 3 = 3)']);
  });

  it('wraps a long membership set every 5 elements (canvas only)', () => {
    const v2: ConditionLogicTree = {
      version: 2,
      root: { kind: 'cmp', id: 'c1', op: 'in', a: { kind: 'ref', type: 'number', ref: '{{ $variables.counter }}' }, b: { kind: 'lit', type: 'string', value: '4, 1, 2, 0, 3, 4, 5, 6, 6, 7, 7, 8, 8, 9, 6' } },
    };
    expect(summarizeConditionLines(v2)).toEqual(['counter ∈ {4, 1, 2, 0, 3,\n    4, 5, 6, 6, 7,\n    7, 8, 8, 9, 6}']);
  });

  it('keeps a short set (≤5) on one line', () => {
    const v2: ConditionLogicTree = {
      version: 2,
      root: { kind: 'cmp', id: 'c1', op: 'in', a: { kind: 'ref', type: 'number', ref: '{{ $variables.counter }}' }, b: { kind: 'lit', type: 'string', value: '4, 1, 2, 0, 3' } },
    };
    expect(summarizeConditionLines(v2)).toEqual(['counter ∈ {4, 1, 2, 0, 3}']);
  });

  it('returns a single line for a bare comparator (no connector)', () => {
    const v2: ConditionLogicTree = {
      version: 2,
      root: { kind: 'cmp', id: 'c1', op: 'gt', a: { kind: 'ref', type: 'number', ref: '{{ $variables.counter }}' }, b: { kind: 'lit', type: 'number', value: 4 } },
    };
    expect(summarizeConditionLines(v2)).toEqual(['counter > 4']);
  });

  it('returns null for invalid logic', () => {
    expect(summarizeConditionLines(undefined)).toBeNull();
    expect(summarizeConditionLines({ foo: 'bar' })).toBeNull();
  });
});
