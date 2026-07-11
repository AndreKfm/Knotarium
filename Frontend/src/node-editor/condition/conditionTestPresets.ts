// Test-mode presets: derive temporary signal-value samples that force each comparator to pass or fail,
// so an author can sanity-check the logic without a real run. Pure — operates on the draft tree and
// returns a { ref -> sample } map fed to the manual preview provider; the evaluator coerces the string
// samples per the operand's declared type (so storing strings here is fine).

import type { DraftNode, DraftTree } from './conditionTree';

export type SampleType = 'string' | 'number' | 'boolean';

/** One comparator leaf reduced to what a preset needs: the ref operand to drive + its comparand/op. */
export interface TestLeaf {
  id: string;
  op: string;
  /** The reference operand's expression (the signal value we can simulate). */
  ref: string;
  refType: SampleType;
  /** The fixed literal comparand on the other side ('' when unary / no literal). */
  comparand: string;
}

/** Comparator leaves (depth-first) that have a ref operand we can drive in test mode. */
export function comparatorLeaves(draft: DraftTree): TestLeaf[] {
  const out: TestLeaf[] = [];
  const walk = (n: DraftNode | null | undefined): void => {
    if (!n) return;
    if (n.kind === 'cmp') {
      const operands = [n.a, n.b].filter(Boolean) as NonNullable<typeof n.b>[];
      const refOp = operands.find((o) => o.kind === 'ref');
      const litOp = operands.find((o) => o.kind === 'lit');
      if (refOp && refOp.kind === 'ref') {
        out.push({
          id: n.id,
          op: n.op,
          ref: refOp.ref,
          refType: refOp.type,
          comparand: litOp && litOp.kind === 'lit' ? litOp.text : '',
        });
      }
      return;
    }
    if (n.kind === 'group') {
      n.children.forEach(walk);
      return;
    }
    walk(n.child);
  };
  walk(draft.root);
  return out;
}

// Produce a value that is definitely DIFFERENT from `v` for the given type.
function mutate(v: string, type: SampleType): string {
  if (type === 'boolean') return v.trim().toLowerCase() === 'true' ? 'false' : 'true';
  if (type === 'number') { const n = Number(v); return String(Number.isFinite(n) ? n + 1 : 1); }
  return v.length ? `${v}_x` : 'x';
}
function bump(v: string, delta: number): string { const n = Number(v); return String((Number.isFinite(n) ? n : 0) + delta); }
function firstListItem(list: string): string { return list.split(',')[0]?.trim() ?? ''; }

/** A signal-value sample (as a string) that makes `leaf` evaluate to `want`. Best-effort across operators. */
export function sampleForLeaf(leaf: TestLeaf, want: 'pass' | 'fail'): string {
  const { op, comparand: c, refType } = leaf;
  const pass = want === 'pass';
  switch (op) {
    case 'eq': return pass ? c : mutate(c, refType);
    case 'ne': return pass ? mutate(c, refType) : c;
    case 'gt': return pass ? bump(c, 1) : c;       // ref > c
    case 'gte': return pass ? c : bump(c, -1);     // ref >= c
    case 'lt': return pass ? bump(c, -1) : c;      // ref < c
    case 'lte': return pass ? c : bump(c, 1);      // ref <= c
    case 'true': return pass ? 'true' : 'false';
    case 'false': return pass ? 'false' : 'true';
    case 'contains': case 'starts': case 'ends': return pass ? (c || 'x') : (c ? mutate(c, 'string') : '');
    case 'ncontains': return pass ? '' : (c || 'x');
    case 'exists': case 'nempty': return pass ? (c || 'x') : '';
    case 'nexists': case 'empty': return pass ? '' : (c || 'x');
    case 'in': return pass ? (firstListItem(c) || 'x') : mutate(firstListItem(c), refType);
    case 'nin': return pass ? mutate(firstListItem(c), refType) : (firstListItem(c) || 'x');
    default: return pass ? c : mutate(c, refType);
  }
}

export type Preset =
  | { kind: 'allPass' }
  | { kind: 'allFail' }
  | { kind: 'failOne'; id: string }; // that comparator fails, the rest pass (the "X mismatch" scenarios)

/** Build the { ref -> sample } map for a preset. */
export function presetSamples(draft: DraftTree, preset: Preset): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const leaf of comparatorLeaves(draft)) {
    const want: 'pass' | 'fail' =
      preset.kind === 'allPass' ? 'pass'
      : preset.kind === 'allFail' ? 'fail'
      : leaf.id === preset.id ? 'fail' : 'pass';
    out[leaf.ref] = sampleForLeaf(leaf, want);
  }
  return out;
}
