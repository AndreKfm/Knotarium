import { describe, expect, it } from 'vitest';
import { createNodePackageMetadataMap, parseSockets } from './nodePackages';
import type { NodePackageSummary } from '../types';

describe('createNodePackageMetadataMap', () => {
  it('parses PascalCase manifest fields for built-in trigger nodes', () => {
    const metadata = createNodePackageMetadataMap([
      {
        id: 'scheduler',
        displayName: 'Scheduler',
        category: 'Triggers',
        versions: [
          {
            id: 'scheduler@1.0.0',
            nodePackageId: 'scheduler',
            version: '1.0.0',
            manifestJson: JSON.stringify({
              Id: 'scheduler',
              DisplayName: 'Cron Scheduler',
              Category: 'Triggers',
              TriggerOnly: true,
              Outputs: [{ Name: 'triggeredAt' }],
            }),
            source: 'builtin',
            capabilities: [],
            createdAt: '2026-05-31T00:00:00Z',
          },
        ],
      } satisfies NodePackageSummary,
    ]);

    expect(metadata.scheduler).toEqual({
      displayName: 'Cron Scheduler',
      triggerOnly: true,
      outputHandles: ['triggeredAt'],
    });
  });
});

describe('parseSockets', () => {
  it('preserves declared socket type and field schema (camelCase)', () => {
    expect(parseSockets([
      { name: 'result', type: 'object', fields: [
        { name: 'statusCode', type: 'number', required: true },
        { name: 'body', type: 'string' },
      ] },
    ])).toEqual([
      { name: 'result', type: 'object', fields: [
        { name: 'statusCode', type: 'number', required: true },
        { name: 'body', type: 'string', required: undefined },
      ] },
    ]);
  });

  it('reads PascalCase keys from the .NET serializer', () => {
    expect(parseSockets([{ Name: 'success', Type: 'any', Fields: [{ Name: 'id', Type: 'string' }] }])).toEqual([
      { name: 'success', type: 'any', fields: [{ name: 'id', type: 'string', required: undefined }] },
    ]);
  });

  it('keeps a name-only socket and drops empty/invalid entries', () => {
    expect(parseSockets([{ name: 'out' }, null, {}, 'nope'])).toEqual([
      { name: 'out', type: undefined, fields: undefined },
    ]);
  });

  it('returns [] for non-array input', () => {
    expect(parseSockets(undefined)).toEqual([]);
    expect(parseSockets({})).toEqual([]);
  });
});