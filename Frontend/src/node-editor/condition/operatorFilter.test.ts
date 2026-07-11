import { describe, expect, it } from 'vitest';
import { checkOrderingTypes, operatorsForType, ordinalStringHint } from './operatorFilter';

describe('operatorsForType (type-aware filtering)', () => {
  it('offers every operator when the type is unknown (any)', () => {
    expect(operatorsForType('any')).toHaveLength(19);
  });

  it('hides text operators for a number left operand', () => {
    const ids = operatorsForType('number').map((o) => o.id);
    expect(ids).toContain('gt');
    expect(ids).toContain('in');
    expect(ids).not.toContain('contains');
    expect(ids).not.toContain('starts');
  });

  it('keeps `any`-accepting existence ops for every type', () => {
    for (const t of ['string', 'number', 'boolean'] as const) {
      const ids = operatorsForType(t).map((o) => o.id);
      expect(ids).toContain('exists');
      expect(ids).toContain('nexists');
    }
  });

  it('offers boolean ops only where boolean is accepted', () => {
    expect(operatorsForType('boolean').map((o) => o.id)).toContain('true');
    expect(operatorsForType('number').map((o) => o.id)).not.toContain('true');
  });

  it('preserves catalog order', () => {
    const ids = operatorsForType('number').map((o) => o.id);
    expect(ids.indexOf('eq')).toBeLessThan(ids.indexOf('gt'));
  });
});

describe('checkOrderingTypes (edit-time cross-type ordering block)', () => {
  it('does not constrain non-ordering operators', () => {
    expect(checkOrderingTypes('eq', 'number', 'string').blocked).toBe(false);
  });

  it('allows same-type number and same-type string ordering', () => {
    expect(checkOrderingTypes('gt', 'number', 'number').blocked).toBe(false);
    expect(checkOrderingTypes('lt', 'string', 'string').blocked).toBe(false);
  });

  it('blocks differing known types', () => {
    const r = checkOrderingTypes('gte', 'number', 'string');
    expect(r.blocked).toBe(true);
    expect(r.reason).toMatch(/matching types/);
  });

  it('blocks same-but-non-orderable booleans', () => {
    const r = checkOrderingTypes('gt', 'boolean', 'boolean');
    expect(r.blocked).toBe(true);
    expect(r.reason).toMatch(/can't be ordered/);
  });

  it('does not block when either type is unknown (runtime backstop)', () => {
    expect(checkOrderingTypes('gt', 'any', 'number').blocked).toBe(false);
    expect(checkOrderingTypes('gt', 'string', 'any').blocked).toBe(false);
  });
});

describe('ordinalStringHint', () => {
  it('hints when an ordering operand is a string', () => {
    expect(ordinalStringHint('gt', 'string', 'string')).toMatch(/lexically/);
    expect(ordinalStringHint('lt', 'string', 'any')).toMatch(/lexically/);
  });

  it('is silent for numeric ordering and for non-ordering ops', () => {
    expect(ordinalStringHint('gt', 'number', 'number')).toBeNull();
    expect(ordinalStringHint('eq', 'string', 'string')).toBeNull();
  });
});
