// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { NodeProps } from '@xyflow/react';
import { GenericCustomNode } from '../components/CustomNodes';

export function createNodeTypes(nodeTypeIds: string[]): Record<string, (props: NodeProps) => React.ReactNode> {
  return nodeTypeIds.reduce<Record<string, (props: NodeProps) => React.ReactNode>>((accumulator, nodeTypeId) => {
    accumulator[nodeTypeId] = (props: NodeProps) => <GenericCustomNode {...props} type={nodeTypeId} />;
    return accumulator;
  }, {});
}
