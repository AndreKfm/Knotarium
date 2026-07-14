import { describe, expect, it } from 'vitest';
import { aiClassifyOutputHandles, parseAiClassifyCategories } from './aiClassifyPorts';

// These rules mirror AiClassifyNodeTask.ParseCategories on the backend — if one of these cases
// changes, change it there too, or canvas handles will stop matching runtime ports.
describe('parseAiClassifyCategories', () => {
  it('splits on commas, semicolons and newlines, trimming each label', () => {
    expect(parseAiClassifyCategories(' Billing , Support ;Spam\nOther ')).toEqual([
      'Billing', 'Support', 'Spam', 'Other',
    ]);
  });

  it('drops empties and case-insensitive duplicates, keeping the first spelling', () => {
    expect(parseAiClassifyCategories('a,, A ,b,B,')).toEqual(['a', 'b']);
  });

  it('returns empty for missing or non-string values', () => {
    expect(parseAiClassifyCategories(undefined)).toEqual([]);
    expect(parseAiClassifyCategories('   ')).toEqual([]);
    expect(parseAiClassifyCategories(42)).toEqual([]);
  });
});

describe('aiClassifyOutputHandles', () => {
  it('appends the otherwise fallback after the categories', () => {
    expect(aiClassifyOutputHandles({ categories: 'x, y' })).toEqual(['x', 'y', 'otherwise']);
  });

  it('yields only the fallback when nothing is configured yet', () => {
    expect(aiClassifyOutputHandles(undefined)).toEqual(['otherwise']);
    expect(aiClassifyOutputHandles({})).toEqual(['otherwise']);
  });
});
