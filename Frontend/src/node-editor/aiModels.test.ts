import { describe, it, expect } from 'vitest';
import { curatedModelsFor, mergeModelSuggestions, CURATED_MODELS } from './aiModels';

describe('aiModels', () => {
  it('returns curated models for a known vendor and empty for unknown', () => {
    expect(curatedModelsFor('anthropic')).toEqual(CURATED_MODELS.anthropic);
    expect(curatedModelsFor('azure')).toEqual([]);
    expect(curatedModelsFor('nope')).toEqual([]);
    expect(curatedModelsFor(null)).toEqual([]);
  });

  it('merges curated ∪ live, curated first, de-duplicated, trimmed', () => {
    const merged = mergeModelSuggestions(['a', 'b'], [' b ', 'c', '']);
    expect(merged).toEqual(['a', 'b', 'c']);
  });
});
