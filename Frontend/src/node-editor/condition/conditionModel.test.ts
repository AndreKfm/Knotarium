// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import {
  LEGACY_OPERATOR_NAMES,
  addComparator,
  coerceDraftToLogic,
  legacyToDraft,
  logicToDraft,
  mapLegacyOperator,
  newComparator,
  newCondition,
  removeComparator,
  setOperandKind,
  setOperandType,
  setOperator,
  type ConditionLogic,
  type DraftCondition,
} from './conditionModel';

describe('defaults / construction', () => {
  it('newCondition is a single binary comparator (AND of one)', () => {
    const c = newCondition();
    expect(c.comb).toBe('and');
    expect(c.cmps).toHaveLength(1);
    expect(c.cmps[0].op).toBe('eq');
    expect(c.cmps[0].a).toEqual({ kind: 'lit', type: 'string', text: '' });
    expect(c.cmps[0].b).toEqual({ kind: 'lit', type: 'string', text: '' });
  });

  it('newComparator drops B for a unary operator', () => {
    expect(newComparator('exists').b).toBeNull();
    expect(newComparator('eq').b).not.toBeNull();
  });
});

describe('structural edits', () => {
  it('addComparator appends with a fresh deterministic id', () => {
    const c1 = newCondition();
    const c2 = addComparator(c1);
    expect(c2.cmps.map((c) => c.id)).toEqual(['c1', 'c2']);
    // input is not mutated
    expect(c1.cmps).toHaveLength(1);
  });

  it('addComparator id is max-suffix + 1 (no reuse of removed ids)', () => {
    let c = newCondition();
    c = addComparator(c); // c2
    c = removeComparator(c, 'c1');
    c = addComparator(c); // should be c3, not c2/c1
    expect(c.cmps.map((x) => x.id)).toEqual(['c2', 'c3']);
  });

  it('removeComparator can empty the list (coercion enforces the floor)', () => {
    const c = removeComparator(newCondition(), 'c1');
    expect(c.cmps).toHaveLength(0);
    expect(coerceDraftToLogic(c).issues.some((i) => i.kind === 'structure')).toBe(true);
  });

  it('setOperator drops B going unary and seeds B going binary (of A’s type)', () => {
    const binary = newComparator('eq'); // a/b string lits
    const unary = setOperator(binary, 'exists');
    expect(unary.b).toBeNull();

    const typedA = setOperandType(unary.a, 'number');
    const reBinary = setOperator({ ...unary, a: typedA }, 'gt');
    expect(reBinary.b).toEqual({ kind: 'lit', type: 'number', text: '' });
  });

  it('setOperator forces B to a string literal for a list-right op (Is one of)', () => {
    // A number literal B (e.g. from eq) is re-typed to string so the comma list is held as raw text.
    const numericB = newComparator('eq');
    numericB.b = { kind: 'lit', type: 'number', text: '3' };
    const list = setOperator(numericB, 'in');
    expect(list.b).toEqual({ kind: 'lit', type: 'string', text: '3' });

    // A fresh list op with no prior B seeds an empty string literal.
    const seeded = setOperator({ ...newComparator('exists'), b: null }, 'in');
    expect(seeded.b).toEqual({ kind: 'lit', type: 'string', text: '' });
  });

  it('setOperandKind preserves the declared type and resets content', () => {
    const lit = setOperandType(newComparator('eq').a, 'number'); // number lit
    const ref = setOperandKind(lit, 'ref');
    expect(ref).toEqual({ kind: 'ref', type: 'number', ref: '' });
    expect(setOperandKind(ref, 'ref')).toBe(ref); // no-op identity when unchanged
  });
});

describe('draft → persisted coercion', () => {
  function lit(type: 'string' | 'number' | 'boolean', text: string) {
    return { kind: 'lit' as const, type, text };
  }

  it('coerces a fully-valid draft into typed logic', () => {
    const draft: DraftCondition = {
      comb: 'or',
      cmps: [
        { id: 'c1', op: 'eq', a: lit('number', '12'), b: lit('number', '12') },
        { id: 'c2', op: 'true', a: lit('boolean', 'true'), b: null },
      ],
    };
    const { logic, issues } = coerceDraftToLogic(draft);
    expect(issues).toEqual([]);
    expect(logic).toEqual<ConditionLogic>({
      version: 1,
      comb: 'or',
      cmps: [
        { id: 'c1', op: 'eq', a: { kind: 'lit', type: 'number', value: 12 }, b: { kind: 'lit', type: 'number', value: 12 } },
        { id: 'c2', op: 'true', a: { kind: 'lit', type: 'boolean', value: true } },
      ],
    });
  });

  it('flags an empty literal as unset and yields no logic', () => {
    const draft: DraftCondition = {
      comb: 'and',
      cmps: [{ id: 'c1', op: 'eq', a: lit('string', ''), b: lit('string', 'x') }],
    };
    const { logic, issues } = coerceDraftToLogic(draft);
    expect(logic).toBeNull();
    expect(issues).toContainEqual({ comparatorId: 'c1', operand: 'a', kind: 'unset', message: "Operand 'a' is not set." });
  });

  it('flags a non-numeric number literal as invalid', () => {
    const draft: DraftCondition = {
      comb: 'and',
      cmps: [{ id: 'c1', op: 'eq', a: lit('number', '1,000'), b: lit('number', '1000') }],
    };
    const { issues } = coerceDraftToLogic(draft);
    expect(issues.find((i) => i.operand === 'a')?.kind).toBe('invalid');
  });

  it('flags a binary op missing its B operand as a structural issue', () => {
    const draft: DraftCondition = {
      comb: 'and',
      cmps: [{ id: 'c1', op: 'eq', a: lit('string', 'x'), b: null }],
    };
    const { issues } = coerceDraftToLogic(draft);
    expect(issues).toContainEqual({ comparatorId: 'c1', operand: 'b', kind: 'structure', message: "Operator 'eq' needs a second operand." });
  });

  it('rejects an over-length regex pattern', () => {
    const draft: DraftCondition = {
      comb: 'and',
      cmps: [{ id: 'c1', op: 'regex', a: lit('string', 'x'), b: lit('string', 'a'.repeat(513)) }],
    };
    expect(coerceDraftToLogic(draft).issues.find((i) => i.operand === 'b')?.message).toMatch(/Regex/);
  });
});

describe('round-trip draft ⇄ persisted', () => {
  it('logicToDraft → coerce reproduces the same logic', () => {
    const logic: ConditionLogic = {
      version: 1,
      comb: 'and',
      cmps: [
        { id: 'c1', op: 'gt', a: { kind: 'lit', type: 'number', value: 3.5 }, b: { kind: 'ref', type: 'number', ref: '{{ $variables.x }}' } },
        { id: 'c2', op: 'eq', a: { kind: 'lit', type: 'string', value: 'a,b' }, b: { kind: 'lit', type: 'boolean', value: false } },
        { id: 'c3', op: 'exists', a: { kind: 'ref', type: 'string', ref: '{{ $node.n.output.f }}' } },
      ],
    };
    const back = coerceDraftToLogic(logicToDraft(logic));
    expect(back.issues).toEqual([]);
    expect(back.logic).toEqual(logic);
  });
});

describe('legacy seeding', () => {
  it('maps every shipped legacy operator name', () => {
    for (const name of LEGACY_OPERATOR_NAMES) {
      expect(mapLegacyOperator(name), `legacy '${name}' must map`).not.toBeNull();
    }
    // case-insensitive, not-equals → ne
    expect(mapLegacyOperator('notequal')).toBe('ne');
    expect(mapLegacyOperator('GREATERTHANOREQUAL')).toBe('gte');
  });

  it('returns no draft for an unmappable operator', () => {
    expect(legacyToDraft({ operator: 'Frobnicate', left: 'a', right: 'b' })).toEqual({
      draft: null,
      operatorMapped: false,
    });
  });

  it('seeds refs from {{ }} expressions and literals with inferred types', () => {
    const ref = legacyToDraft({ operator: 'Equal', left: '{{ $variables.count }}', right: '5' });
    expect(ref.operatorMapped).toBe(true);
    expect(ref.draft!.cmps[0].op).toBe('eq');
    expect(ref.draft!.cmps[0].a).toEqual({ kind: 'ref', type: 'string', ref: '{{ $variables.count }}' });
    expect(ref.draft!.cmps[0].b).toEqual({ kind: 'lit', type: 'number', text: '5' });

    const bools = legacyToDraft({ operator: 'NotEqual', left: 'true', right: 'hello' });
    expect(bools.draft!.cmps[0].a).toEqual({ kind: 'lit', type: 'boolean', text: 'true' });
    expect(bools.draft!.cmps[0].b).toEqual({ kind: 'lit', type: 'string', text: 'hello' });
  });

  it('an empty legacy operand seeds an unset (empty) string literal', () => {
    const seed = legacyToDraft({ operator: 'Contains', left: '', right: 'x' });
    expect(seed.draft!.cmps[0].a).toEqual({ kind: 'lit', type: 'string', text: '' });
    // and that unset operand blocks coercion
    expect(coerceDraftToLogic(seed.draft!).logic).toBeNull();
  });
});
