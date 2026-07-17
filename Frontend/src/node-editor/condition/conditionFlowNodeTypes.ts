// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// The @xyflow/react nodeTypes map, in its own module so ConditionFlowNodes.tsx exports only components
// (Fast Refresh / react-refresh lint rule).

import { ComparatorNode, GroupNode, InputNode, NotNode, OutputNode, PlaceholderNode } from './ConditionFlowNodes';

export const conditionFlowNodeTypes = {
  input: InputNode,
  comparator: ComparatorNode,
  group: GroupNode,
  not: NotNode,
  output: OutputNode,
  placeholder: PlaceholderNode,
};
