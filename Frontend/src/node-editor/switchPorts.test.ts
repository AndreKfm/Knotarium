// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import { parseSwitchCases, switchOutputHandles } from './switchPorts';

// These rules mirror SwitchNodeTask on the backend — if one of these cases changes, change it
// there too, or canvas handles will stop matching runtime ports.
describe('parseSwitchCases', () => {
  it('keeps a single case as-is', () => {
    expect(parseSwitchCases('Gold')).toEqual(['Gold']);
  });

  it('splits on commas, semicolons and newlines, trimming each case', () => {
    expect(parseSwitchCases(' Gold , Silver ;Bronze\nTin\r\nLead ')).toEqual([
      'Gold', 'Silver', 'Bronze', 'Tin', 'Lead',
    ]);
  });

  it('drops empties and case-insensitive duplicates, keeping the first spelling', () => {
    expect(parseSwitchCases('a,, A ,b,B,')).toEqual(['a', 'b']);
  });

  it('returns empty for missing or non-string values', () => {
    expect(parseSwitchCases(undefined)).toEqual([]);
    expect(parseSwitchCases('   ')).toEqual([]);
    expect(parseSwitchCases(42)).toEqual([]);
  });
});

describe('switchOutputHandles', () => {
  it('appends the default fallback after the cases', () => {
    expect(switchOutputHandles({ cases: 'x, y' })).toEqual(['x', 'y', 'default']);
  });

  it('yields only the fallback when nothing is configured yet', () => {
    expect(switchOutputHandles(undefined)).toEqual(['default']);
    expect(switchOutputHandles({})).toEqual(['default']);
  });

  // The backend routes a value matching 'default' to the fallback port, so it must not also
  // render as its own handle.
  it('does not duplicate the fallback when a case is spelled like it', () => {
    expect(switchOutputHandles({ cases: 'x, Default' })).toEqual(['x', 'default']);
  });
});
