import { describe, expect, it } from 'vitest';
import { loadRepoJson } from '../../test/repoFixture';
import { OPERATORS, OPERATOR_GROUPS } from './operators';

// Drift guard (B2): the FE catalog must equal test-fixtures/condition/condition-catalog.fixture.json, the shared
// FE/BE source of truth. Mirrors the backend ConditionOperatorCatalogTests so "what you see == what
// runs" holds across both languages. Loads the SAME file the backend test loads (linked, not copied).

interface CatalogEntry {
  id: string;
  group: string;
  label: string;
  symbol: string;
  arity: 'unary' | 'binary';
  accepts: string[];
  rightKind?: 'list';
}

interface CatalogFixture {
  version: number;
  operators: CatalogEntry[];
  groups: string[];
}

// vitest runs with cwd = Frontend/; the shared fixture lives at the repo-root test-fixtures/condition/.
const fixture = loadRepoJson<CatalogFixture>('../test-fixtures/condition/condition-catalog.fixture.json');

describe('condition operator catalog drift', () => {
  it('has the same operator ids, in the same order, as the fixture', () => {
    expect(OPERATORS.map((o) => o.id)).toEqual(fixture.operators.map((o) => o.id));
  });

  it('matches the fixture group order', () => {
    expect([...OPERATOR_GROUPS]).toEqual(fixture.groups);
  });

  it.each(fixture.operators.map((o) => [o.id, o] as const))(
    'operator %s matches the fixture (group/label/symbol/arity/accepts/rightKind)',
    (id, expected) => {
      const actual = OPERATORS.find((o) => o.id === id);
      expect(actual, `operator '${id}' missing from FE catalog`).toBeDefined();
      expect(actual!.group).toBe(expected.group);
      expect(actual!.label).toBe(expected.label);
      expect(actual!.symbol).toBe(expected.symbol);
      expect(actual!.arity).toBe(expected.arity);
      expect(actual!.accepts).toEqual(expected.accepts);
      // rightKind is optional in both; normalize undefined for the comparison.
      expect(actual!.rightKind ?? null).toBe(expected.rightKind ?? null);
    },
  );

  it('introduces no operators absent from the fixture', () => {
    const fixtureIds = new Set(fixture.operators.map((o) => o.id));
    for (const o of OPERATORS) {
      expect(fixtureIds.has(o.id), `FE operator '${o.id}' not in fixture`).toBe(true);
    }
  });
});
