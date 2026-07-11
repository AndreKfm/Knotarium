import type { Node as RFNode } from '@xyflow/react';
import type { NodePackageMetadata } from '../utils/nodePackages';
import { FORLOOP_DEFAULT_WIDTH, FORLOOP_DEFAULT_HEIGHT, isContainerNodeType } from './canvasGeometry';

/** Unique-enough id for a freshly dropped node of the given type. */
export function createNodeId(type: string): string {
  return `${type}-${Math.random().toString(36).substring(2, 11)}`;
}

export interface BuildNodeOptions {
  type: string;
  position: { x: number; y: number };
  metadata?: NodePackageMetadata;
  /** Display name to use when metadata has none. */
  fallbackDisplayName: string;
  /** Initial node properties (e.g. { operationId } for an OpenAPI operation). */
  properties?: Record<string, unknown>;
  /** When set, the node is created as a child of this container (extent 'parent'). */
  parentId?: string;
}

/**
 * Single source of truth for constructing a React Flow node from a package + metadata.
 * Consolidates the previously duplicated node-building across the palette drop,
 * OpenAPI-operation drop, and programmatic addNode paths.
 */
export function buildNode(options: BuildNodeOptions): RFNode {
  const { type, position, metadata, fallbackDisplayName, properties = {}, parentId } = options;
  return {
    id: createNodeId(type),
    type,
    position,
    ...(parentId ? { parentId, extent: 'parent' as const } : {}),
    ...(isContainerNodeType(type) ? { style: { width: FORLOOP_DEFAULT_WIDTH, height: FORLOOP_DEFAULT_HEIGHT } } : {}),
    data: {
      properties,
      displayName: metadata?.displayName || fallbackDisplayName,
      triggerOnly: metadata?.triggerOnly ?? false,
      outputHandles: metadata?.outputHandles || ['result'],
    },
  };
}
