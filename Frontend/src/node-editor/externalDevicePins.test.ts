// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import {
  readDevicePins,
  readDeviceSurface,
  deviceHandleIds,
  eventPinHandleId,
  actionPinHandleId,
} from './externalDevicePins';

describe('readDevicePins', () => {
  it('reads the DynamicOptionMultiValue shape ({ mode, items })', () => {
    const raw = { mode: 'list', items: [{ value: 'VehicleRecognised', label: 'Vehicle recognised' }, { value: 'MotionDetected' }] };
    expect(readDevicePins(raw)).toEqual([
      { value: 'VehicleRecognised', label: 'Vehicle recognised' },
      { value: 'MotionDetected', label: 'MotionDetected' },
    ]);
  });

  it('reads a bare array of strings or { value, label } objects', () => {
    expect(readDevicePins(['A', { value: 'B', label: 'Bee' }])).toEqual([
      { value: 'A', label: 'A' },
      { value: 'B', label: 'Bee' },
    ]);
  });

  it('reads a single string / object', () => {
    expect(readDevicePins('Solo')).toEqual([{ value: 'Solo', label: 'Solo' }]);
    expect(readDevicePins({ value: 'X', label: 'Ex' })).toEqual([{ value: 'X', label: 'Ex' }]);
  });

  it('returns [] for empty/nullish input', () => {
    expect(readDevicePins(null)).toEqual([]);
    expect(readDevicePins(undefined)).toEqual([]);
    expect(readDevicePins('')).toEqual([]);
    expect(readDevicePins({ mode: 'list', items: [] })).toEqual([]);
  });

  it('dedupes by value and ignores blank/non-string values', () => {
    const raw = { items: [{ value: 'A' }, { value: 'A', label: 'dup' }, { value: '   ' }, { value: 42 }] };
    expect(readDevicePins(raw)).toEqual([{ value: 'A', label: 'A' }]);
  });
});

describe('readDeviceSurface', () => {
  it('splits eventPins / actionPins off the properties bag', () => {
    const surface = readDeviceSurface({
      eventPins: { items: [{ value: 'E1' }] },
      actionPins: ['A1', 'A2'],
    });
    expect(surface.events).toEqual([{ value: 'E1', label: 'E1' }]);
    expect(surface.actions).toEqual([{ value: 'A1', label: 'A1' }, { value: 'A2', label: 'A2' }]);
  });

  it('tolerates missing/empty properties', () => {
    expect(readDeviceSurface(undefined)).toEqual({ events: [], actions: [] });
    expect(readDeviceSurface({})).toEqual({ events: [], actions: [] });
  });
});

describe('deviceHandleIds', () => {
  it('produces source handle ids for both events and incoming actions (no inputs)', () => {
    const { sourceHandles, targetHandles } = deviceHandleIds({
      eventPins: ['Door forced'],
      actionPins: ['Start recording'],
    });
    // Pure inbound surface: events AND actions are sources; the block has no input pins.
    expect(sourceHandles.has(eventPinHandleId('Door forced'))).toBe(true);
    expect(sourceHandles.has(actionPinHandleId('Start recording'))).toBe(true);
    expect(sourceHandles.has('evt:Door forced')).toBe(true);
    expect(sourceHandles.has('act:Start recording')).toBe(true);
    expect(targetHandles.size).toBe(0);
  });
});
