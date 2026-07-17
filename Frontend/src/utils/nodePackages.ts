// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { Node as RFNode } from '@xyflow/react';
import type { NodeFieldSchema, NodePackageManifestSummary, NodePackageSummary, NodeSocketSchema } from '../types';

export interface NodePackageMetadata {
  displayName: string;
  triggerOnly: boolean;
  outputHandles: string[];
}

type RawManifest = Record<string, unknown>;

function getLatestVersion(nodePackage: NodePackageSummary) {
  return [...(nodePackage.versions || [])].sort((left, right) => {
    return new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime();
  })[0];
}

function parseManifest(nodePackage: NodePackageSummary): NodePackageManifestSummary | null {
  const latestVersion = getLatestVersion(nodePackage);
  if (!latestVersion?.manifestJson) {
    return null;
  }

  try {
    const manifest = JSON.parse(latestVersion.manifestJson) as RawManifest;

    return {
      id: typeof manifest.id === 'string' ? manifest.id : typeof manifest.Id === 'string' ? manifest.Id : nodePackage.id,
      displayName: typeof manifest.displayName === 'string'
        ? manifest.displayName
        : typeof manifest.DisplayName === 'string'
          ? manifest.DisplayName
          : nodePackage.displayName,
      category: typeof manifest.category === 'string'
        ? manifest.category
        : typeof manifest.Category === 'string'
          ? manifest.Category
          : nodePackage.category,
      triggerOnly: typeof manifest.triggerOnly === 'boolean'
        ? manifest.triggerOnly
        : typeof manifest.TriggerOnly === 'boolean'
          ? manifest.TriggerOnly
          : undefined,
      outputs: parseSockets(manifest.outputs ?? manifest.Outputs),
      inputs: parseSockets(manifest.inputs ?? manifest.Inputs),
    } satisfies NodePackageManifestSummary;
  } catch {
    return null;
  }
}

/** Read a manifest's inputs/outputs array, preserving each socket's declared type + field schema
 *  (tolerant of camelCase or PascalCase keys, as manifests come from both TS and .NET serializers). */
export function parseSockets(raw: unknown): NodeSocketSchema[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const readString = (obj: Record<string, unknown>, camel: string, pascal: string): string | undefined => {
    const value = obj[camel] ?? obj[pascal];
    return typeof value === 'string' ? value : undefined;
  };
  return raw
    .filter((socket): socket is Record<string, unknown> => !!socket && typeof socket === 'object')
    .map(socket => {
      const rawFields = socket.fields ?? socket.Fields;
      const fields: NodeFieldSchema[] | undefined = Array.isArray(rawFields)
        ? rawFields
            .filter((field): field is Record<string, unknown> => !!field && typeof field === 'object')
            .map(field => ({
              name: readString(field, 'name', 'Name') ?? '',
              type: readString(field, 'type', 'Type'),
              required: typeof (field.required ?? field.Required) === 'boolean'
                ? (field.required ?? field.Required) as boolean
                : undefined,
            }))
            .filter(field => field.name.length > 0)
        : undefined;
      return {
        name: readString(socket, 'name', 'Name') ?? '',
        type: readString(socket, 'type', 'Type'),
        fields: fields && fields.length > 0 ? fields : undefined,
      } satisfies NodeSocketSchema;
    })
    .filter(socket => socket.name.length > 0);
}

export function createNodePackageMetadataMap(nodePackages: NodePackageSummary[]): Record<string, NodePackageMetadata> {
  return nodePackages.reduce<Record<string, NodePackageMetadata>>((accumulator, nodePackage) => {
    const manifest = parseManifest(nodePackage);
    accumulator[nodePackage.id] = {
      displayName: manifest?.displayName || nodePackage.displayName,
      triggerOnly: Boolean(manifest?.triggerOnly),
      outputHandles: (manifest?.outputs || []).map(output => output.name).filter(Boolean),
    };
    return accumulator;
  }, {});
}

// Stamp each subflow node with the name of the workflow it references (data.subflowName), resolved
// from an id -> name map. Display-only — not persisted. Used by both the editor and the run view so
// a subflow node reads "Sub1" instead of the generic "Subflow".
export function applySubflowNames(
  nodes: RFNode[],
  nameById: Record<string, string>): RFNode[] {
  return nodes.map((node) => {
    if (node.type !== 'subflow') {
      return node;
    }
    const subflowId = (node.data?.properties as Record<string, unknown> | undefined)?.subflowId;
    const resolved = typeof subflowId === 'string' ? (nameById[subflowId] ?? '') : '';
    if ((node.data?.subflowName ?? '') === resolved) {
      return node;
    }
    return { ...node, data: { ...node.data, subflowName: resolved } };
  });
}

export function enrichNodesWithPackageMetadata(
  nodes: RFNode[],
  metadataMap: Record<string, NodePackageMetadata>) : RFNode[] {
  return nodes.map(node => {
    const metadata = metadataMap[node.type || ''];
    if (!metadata) {
      return node;
    }

    return {
      ...node,
      data: {
        ...node.data,
        displayName: metadata.displayName,
        triggerOnly: metadata.triggerOnly,
        outputHandles: metadata.outputHandles,
      },
    };
  });
}