import { describe, expect, it } from 'vitest';
import { aiRouterOutputHandles, parseAiRouterCategories } from './aiRouterPorts';

// These rules mirror AiRouterNodeTask.ParseCategories on the backend — if one of these cases
// changes, change it there too, or canvas handles will stop matching runtime ports.
describe('parseAiRouterCategories', () => {
  it('splits on commas, semicolons and newlines, trimming each label', () => {
    expect(parseAiRouterCategories(' Billing , Support ;Spam\nOther ')).toEqual([
      'Billing', 'Support', 'Spam', 'Other',
    ]);
  });

  it('drops empties and case-insensitive duplicates, keeping the first spelling', () => {
    expect(parseAiRouterCategories('a,, A ,b,B,')).toEqual(['a', 'b']);
  });

  it('returns empty for missing or non-string values', () => {
    expect(parseAiRouterCategories(undefined)).toEqual([]);
    expect(parseAiRouterCategories('   ')).toEqual([]);
    expect(parseAiRouterCategories(42)).toEqual([]);
  });
});

describe('aiRouterOutputHandles', () => {
  it('appends the otherwise fallback after the categories', () => {
    expect(aiRouterOutputHandles({ categories: 'x, y' })).toEqual(['x', 'y', 'otherwise']);
  });

  it('yields only the fallback when nothing is configured yet', () => {
    expect(aiRouterOutputHandles(undefined)).toEqual(['otherwise']);
    expect(aiRouterOutputHandles({})).toEqual(['otherwise']);
  });
});
