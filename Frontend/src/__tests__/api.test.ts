// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../utils/api';
import type { Node as FlowNode, Edge as FlowEdge } from '@xyflow/react';

describe('Api_PublishWorkflow_IncludesPositionMetadata', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('PublishWorkflow_WithCoordinates_SendsMetadataPayloadCorrectly', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      text: async () => JSON.stringify({
        id: 'ver-123',
        workflowDefinitionId: { value: 'wf-abc' },
        versionNumber: 1,
        nodes: [],
        edges: [],
        createdAt: '2026-06-04T00:00:00Z'
      }),
    });
    vi.stubGlobal('fetch', fetchMock);

    const nodes: FlowNode[] = [
      {
        id: 'node-1',
        type: 'log',
        position: { x: 120, y: 340 },
        data: { properties: { message: 'hello' } },
      },
    ];
    const edges: FlowEdge[] = [];

    await api.publishWorkflow('wf-abc', nodes, edges);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/workflows/wf-abc/publish');
    expect(options.method).toBe('POST');
    
    const body = JSON.parse(options.body as string);
    expect(body.nodes).toHaveLength(1);
    expect(body.nodes[0].id.value).toBe('node-1');
    expect(body.nodes[0].properties._metadata.x).toBe(120);
    expect(body.nodes[0].properties._metadata.y).toBe(340);
    expect(body.nodes[0].properties.message).toBe('hello');
  });

  it('SaveWorkflowDraft_WithCoordinates_SendsMetadataPayloadCorrectly', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      text: async () => JSON.stringify({
        id: 'ver-123',
        workflowDefinitionId: { value: 'wf-abc' },
        versionNumber: 1,
        nodes: [],
        edges: [],
        createdAt: '2026-06-04T00:00:00Z'
      }),
    });
    vi.stubGlobal('fetch', fetchMock);

    const nodes: FlowNode[] = [
      {
        id: 'node-2',
        type: 'scheduler',
        position: { x: 50, y: 150 },
        data: { properties: { cronExpression: '*/5 * * * *' } },
      },
    ];
    const edges: FlowEdge[] = [];

    await api.saveWorkflowDraft('wf-abc', nodes, edges);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/workflows/wf-abc/versions');
    
    const body = JSON.parse(options.body as string);
    expect(body.nodes).toHaveLength(1);
    expect(body.nodes[0].id.value).toBe('node-2');
    expect(body.nodes[0].properties._metadata.x).toBe(50);
    expect(body.nodes[0].properties._metadata.y).toBe(150);
    expect(body.nodes[0].properties.cronExpression).toBe('*/5 * * * *');
  });
});

describe('Api_WorkflowVersions_PaginatedEnvelope', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('getWorkflowVersions unwraps the envelope to an array of summaries', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      text: async () => JSON.stringify({
        items: [
          {
            id: 'ver-2',
            versionNumber: 2,
            createdAt: '2026-06-04T00:00:00Z',
            createdBy: null,
            label: null,
            origin: 'Published',
            isActive: true,
            restoredFromVersionId: null,
            nodeCount: 3,
            executionCount: 0,
          },
        ],
        page: 1,
        pageSize: 50,
        totalCount: 1,
      }),
    });
    vi.stubGlobal('fetch', fetchMock);

    const versions = await api.getWorkflowVersions('wf-1');

    expect(fetchMock).toHaveBeenCalledWith('/api/workflows/wf-1/versions');
    expect(Array.isArray(versions)).toBe(true);
    expect(versions).toHaveLength(1);
    expect(versions[0].versionNumber).toBe(2);
    expect(versions[0].origin).toBe('Published');
    expect(versions[0].isActive).toBe(true);
  });

  it('getWorkflowVersionsPage passes page + pageSize and returns the envelope', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      text: async () => JSON.stringify({ items: [], page: 2, pageSize: 25, totalCount: 40 }),
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await api.getWorkflowVersionsPage('wf-1', 2, 25);

    expect(fetchMock).toHaveBeenCalledWith('/api/workflows/wf-1/versions?page=2&pageSize=25');
    expect(result.page).toBe(2);
    expect(result.pageSize).toBe(25);
    expect(result.totalCount).toBe(40);
  });

  it('getWorkflowVersionDetail hits the detail endpoint and maps nodes/edges', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      text: async () => JSON.stringify({
        id: 'ver-2',
        workflowDefinitionId: { value: 'wf-1' },
        versionNumber: 2,
        nodes: [{ id: { value: 'n1' }, type: 'log', properties: {} }],
        edges: [],
        createdAt: '2026-06-04T00:00:00Z',
      }),
    });
    vi.stubGlobal('fetch', fetchMock);

    const version = await api.getWorkflowVersionDetail('wf-1', 'ver-2');

    expect(fetchMock).toHaveBeenCalledWith('/api/workflows/wf-1/versions/ver-2');
    expect(version.versionNumber).toBe(2);
    expect(version.nodes).toHaveLength(1);
    expect(version.nodes[0].id.value).toBe('n1');
  });
});
