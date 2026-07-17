// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { variablePathHead, hasVariablePath, pathContainerKind } from '../utils/variablePath';

describe('variablePathHead', () => {
  it('returns the whole name when there is no path', () => {
    expect(variablePathHead('counter')).toBe('counter');
  });

  it('returns the head before a bracket key', () => {
    expect(variablePathHead('myDict["name"]')).toBe('myDict');
  });

  it('returns the head before an index', () => {
    expect(variablePathHead('list[0]')).toBe('list');
  });

  it('returns the head before a dotted member', () => {
    expect(variablePathHead('config.servers[2].host')).toBe('config');
  });

  it('trims surrounding whitespace', () => {
    expect(variablePathHead('  myDict["a"] ')).toBe('myDict');
  });
});

describe('hasVariablePath', () => {
  it('is false for a bare name', () => {
    expect(hasVariablePath('counter')).toBe(false);
  });

  it('is true for a keyed reference', () => {
    expect(hasVariablePath('myDict["name"]')).toBe(true);
    expect(hasVariablePath('list[0]')).toBe(true);
    expect(hasVariablePath('a.b')).toBe(true);
  });
});

describe('pathContainerKind', () => {
  it('is undefined for a bare name', () => {
    expect(pathContainerKind('counter')).toBeUndefined();
  });

  it('is object for a string key or dotted member', () => {
    expect(pathContainerKind('myDict["name"]')).toBe('object');
    expect(pathContainerKind("myDict['name']")).toBe('object');
    expect(pathContainerKind('config.servers[0]')).toBe('object');
  });

  it('is array for an integer index', () => {
    expect(pathContainerKind('list[0]')).toBe('array');
    expect(pathContainerKind('list[12]')).toBe('array');
  });
});
