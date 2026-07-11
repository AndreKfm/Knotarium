// Edit-time operator helpers for the inline OperatorMenu and the ordering guardrails (spec §4).
// Pure; the UI sits on top. Two named FIXes live here:
//   • type-aware operator filtering — when the left operand's type is known, only operators whose
//     `accepts` includes it (or `any`) are offered.
//   • ordinal-string ordering HINT + edit-time cross-type ordering BLOCK — ordering ops over a string
//     show a lexical-compare hint; an edit-time-known type mismatch is flagged and must not persist
//     (the runtime TYPE_MISMATCH is the dynamic-ref backstop).

import type { OperandType } from './conditionEval';
import { OPERATORS, type OperatorAccept, type OperatorDef } from './operators';

/** A type known at edit time, or `any` when the operand's type can't be determined. */
export type KnownType = OperandType | 'any';

const ORDERING = new Set(['gt', 'gte', 'lt', 'lte']);

export function isOrderingOperator(op: string): boolean {
  return ORDERING.has(op);
}

/**
 * Operators offered for a left operand of the given type. `any` (unknown type) offers everything.
 *
 * For a KNOWN type, `any` in an operator's `accepts` means "also applies when the type is unknown",
 * NOT "matches every concrete type" — otherwise filtering would be a no-op, since nearly every
 * operator carries `any`. So an operator qualifies only if its `accepts` includes the concrete type,
 * unless it is purely `['any']` (the genuinely type-agnostic existence ops exists/nexists). This makes
 * a `number` left operand hide the text ops, a `boolean` hide ordering, etc. Preserves catalog order.
 */
export function operatorsForType(leftType: KnownType): OperatorDef[] {
  if (leftType === 'any') return [...OPERATORS];
  const t = leftType as OperatorAccept;
  return OPERATORS.filter((o) => o.accepts.includes(t) || o.accepts.every((a) => a === 'any'));
}

export interface OrderingTypeCheck {
  blocked: boolean;
  reason?: string;
}

/**
 * Edit-time cross-type ordering block (spec §4/§5.1). Only ordering ops are constrained, and only
 * when BOTH operand types are known: orderable pairs are same-type number or same-type string;
 * differing types or same-but-non-orderable (boolean) are blocked here so they never persist. When
 * either type is `any` (dynamic ref) we don't block — the runtime TYPE_MISMATCH is the backstop.
 */
export function checkOrderingTypes(op: string, aType: KnownType, bType: KnownType): OrderingTypeCheck {
  if (!ORDERING.has(op)) return { blocked: false };
  if (aType === 'any' || bType === 'any') return { blocked: false };

  const orderable = aType === bType && (aType === 'number' || aType === 'string');
  if (orderable) return { blocked: false };
  if (aType !== bType) {
    return { blocked: true, reason: `Ordering needs matching types; got ${aType} and ${bType}.` };
  }
  return { blocked: true, reason: `${aType} values can't be ordered.` };
}

/**
 * Ordinal-string ordering hint (spec §4): ordering on a string operand compares lexically by code
 * unit, which surprises (`"9" > "10"` is true, `"Z" < "a"` is true). Returns the hint, or null when
 * not applicable.
 */
export function ordinalStringHint(op: string, aType: KnownType, bType: KnownType): string | null {
  if (!ORDERING.has(op)) return null;
  if (aType === 'string' || bType === 'string') {
    return 'Strings compare lexically (ordinal): "9" > "10" is true, "Z" < "a" is true.';
  }
  return null;
}
