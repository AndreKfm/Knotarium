// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// The frontend operator catalog — the FE half of the shared FE/BE source of truth (B2).
//
// This MUST stay equivalent (per id/group/label/symbol/arity/accepts/rightKind, and group order)
// to test-fixtures/condition/condition-catalog.fixture.json. A drift test (operators.test.ts) asserts it,
// mirroring the backend ConditionOperatorCatalogTests, so "what you see == what runs" holds across
// both languages. Canonical over the prototype's operator-dialog-data.jsx OPERATORS.
//
// Deviations from the prototype (intentional, see docs/design/condition-operator-semantics.md):
//   1. not-equals id is 'ne' (spec §5.1/§8); the prototype called it 'neq'.
//   2. ordering ops gt/gte/lt/lte also accept 'string' (spec §5.1 ordinal string ordering).
// 'accepts' is the editor type-filter vocabulary (a superset of the persisted declared types) and is
// an editor guardrail only (spec §4), never a runtime guarantee.

export type OperatorArity = 'unary' | 'binary';

/** The editor type-filter vocabulary (superset of persisted declared types string|number|boolean). */
export type OperatorAccept = 'string' | 'number' | 'boolean' | 'array' | 'object' | 'any';

export type OperatorGroup = 'Comparison' | 'Text' | 'Membership' | 'Existence' | 'Boolean';

/** One operator catalog entry. `rightKind: 'list'` marks ops whose B operand is a membership list. */
export interface OperatorDef {
  id: string;
  group: OperatorGroup;
  label: string;
  symbol: string;
  arity: OperatorArity;
  accepts: OperatorAccept[];
  rightKind?: 'list';
}

/** Operator order is significant (drift-tested against the fixture). */
export const OPERATORS: readonly OperatorDef[] = [
  { id: 'eq',        group: 'Comparison', label: 'Equals',           symbol: '=', arity: 'binary', accepts: ['string', 'number', 'boolean', 'any'] },
  { id: 'ne',        group: 'Comparison', label: 'Not equals',       symbol: '≠', arity: 'binary', accepts: ['string', 'number', 'boolean', 'any'] },
  { id: 'gt',        group: 'Comparison', label: 'Greater than',     symbol: '>', arity: 'binary', accepts: ['string', 'number', 'any'] },
  { id: 'gte',       group: 'Comparison', label: 'Greater or equal', symbol: '≥', arity: 'binary', accepts: ['string', 'number', 'any'] },
  { id: 'lt',        group: 'Comparison', label: 'Less than',        symbol: '<', arity: 'binary', accepts: ['string', 'number', 'any'] },
  { id: 'lte',       group: 'Comparison', label: 'Less or equal',    symbol: '≤', arity: 'binary', accepts: ['string', 'number', 'any'] },

  { id: 'contains',  group: 'Text', label: 'Contains',         symbol: '∋', arity: 'binary', accepts: ['string', 'array', 'any'] },
  { id: 'ncontains', group: 'Text', label: 'Does not contain', symbol: '∌', arity: 'binary', accepts: ['string', 'array', 'any'] },
  { id: 'starts',    group: 'Text', label: 'Starts with',      symbol: '▸', arity: 'binary', accepts: ['string', 'any'] },
  { id: 'ends',      group: 'Text', label: 'Ends with',        symbol: '◂', arity: 'binary', accepts: ['string', 'any'] },
  { id: 'regex',     group: 'Text', label: 'Matches regex',    symbol: '≈', arity: 'binary', accepts: ['string', 'any'] },

  { id: 'in',  group: 'Membership', label: 'Is one of',     symbol: '∈', arity: 'binary', accepts: ['string', 'number', 'any'], rightKind: 'list' },
  { id: 'nin', group: 'Membership', label: 'Is not one of', symbol: '∉', arity: 'binary', accepts: ['string', 'number', 'any'], rightKind: 'list' },

  { id: 'empty',   group: 'Existence', label: 'Is empty',      symbol: '∅', arity: 'unary', accepts: ['string', 'array', 'object', 'any'] },
  { id: 'nempty',  group: 'Existence', label: 'Is not empty',  symbol: '⊙', arity: 'unary', accepts: ['string', 'array', 'object', 'any'] },
  { id: 'exists',  group: 'Existence', label: 'Exists',        symbol: '✓', arity: 'unary', accepts: ['any'] },
  { id: 'nexists', group: 'Existence', label: 'Does not exist', symbol: '✕', arity: 'unary', accepts: ['any'] },

  { id: 'true',  group: 'Boolean', label: 'Is true',  symbol: 'T', arity: 'unary', accepts: ['boolean', 'any'] },
  { id: 'false', group: 'Boolean', label: 'Is false', symbol: 'F', arity: 'unary', accepts: ['boolean', 'any'] },
] as const;

/** Group display order (drift-tested against the fixture). */
export const OPERATOR_GROUPS: readonly OperatorGroup[] = [
  'Comparison',
  'Text',
  'Membership',
  'Existence',
  'Boolean',
] as const;

const BY_ID = new Map<string, OperatorDef>(OPERATORS.map((o) => [o.id, o]));

export function getOperator(id: string): OperatorDef | undefined {
  return BY_ID.get(id);
}

export function isKnownOperator(id: string): boolean {
  return BY_ID.has(id);
}

/** A binary op carries a B operand; a unary op ignores it (spec §2). */
export function isBinary(id: string): boolean {
  return BY_ID.get(id)?.arity === 'binary';
}

/**
 * A list-right op ('in'/'nin' — "Is one of" / "Is not one of") takes a membership LIST as its B
 * operand: a comma-separated set of values (or a ref that resolves to an array). The editor holds it
 * as raw 'string' text; each element is typed against A's effective type at eval time (§5.3).
 */
export function isListRight(id: string): boolean {
  return BY_ID.get(id)?.rightKind === 'list';
}
