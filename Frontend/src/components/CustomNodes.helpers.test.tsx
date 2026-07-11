import { describe, it, expect } from 'vitest';
import { isLowDetailZoom, LOD_ZOOM_THRESHOLD } from './CustomNodes.helpers';

describe('isLowDetailZoom', () => {
  it('is low-detail below the default threshold', () => {
    expect(isLowDetailZoom(LOD_ZOOM_THRESHOLD - 0.01)).toBe(true);
    expect(isLowDetailZoom(0.1)).toBe(true);
  });

  it('is full-detail at or above the default threshold', () => {
    expect(isLowDetailZoom(LOD_ZOOM_THRESHOLD)).toBe(false);
    expect(isLowDetailZoom(1)).toBe(false);
    expect(isLowDetailZoom(2)).toBe(false);
  });

  it('honours a custom threshold', () => {
    expect(isLowDetailZoom(0.7, 0.8)).toBe(true);
    expect(isLowDetailZoom(0.9, 0.8)).toBe(false);
  });

  it('uses a sensible default threshold', () => {
    expect(LOD_ZOOM_THRESHOLD).toBeGreaterThan(0);
    expect(LOD_ZOOM_THRESHOLD).toBeLessThan(1);
  });
});
