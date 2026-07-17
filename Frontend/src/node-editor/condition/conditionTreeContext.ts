// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Shared context for the v2 nested-box editor (Phase 8.3, N1-a). Kept separate from the node
// components so Fast Refresh stays happy. Carries leaf-editing + tree-structure handlers, the
// inline-popover open state, the data the inline editors need, and the live per-leaf statuses.

import { createContext } from 'react';
import type { Combinator, ConditionError, ConditionStatus } from './conditionEval';
import type { DraftOperand } from './conditionModel';
import type { RefOption } from './InputEditor';

export interface TreeInputTarget {
  nodeId: string;
  slot: 'a' | 'b';
}

export interface ConditionTreeHandlers {
  // Leaf editing.
  onPickOperator: (nodeId: string, op: string) => void;
  onChangeOperand: (nodeId: string, slot: 'a' | 'b', operand: DraftOperand) => void;
  onChangeSample: (ref: string, value: unknown) => void;
  // Tree-structure edits.
  onAddComparator: (groupId: string) => void;
  onAddGroup: (groupId: string) => void;
  onWrapGroup: (nodeId: string) => void;
  onWrapNot: (nodeId: string) => void;
  onSetGroupOp: (groupId: string, op: Combinator) => void;
  onRemove: (nodeId: string) => void;
  onUnwrap: (nodeId: string) => void;
  // Inline-popover open state (one at a time).
  openOperatorFor: string | null;
  openInputFor: TreeInputTarget | null;
  setOpenOperator: (nodeId: string | null) => void;
  setOpenInput: (target: TreeInputTarget | null) => void;
  // Data + live state.
  variables: RefOption[];
  sampleValues: Record<string, unknown>;
  /** Test mode: signal-ref operands become editable inline (simulate the incoming value); literals stay fixed. */
  testMode: boolean;
  leafStatus: Record<string, ConditionStatus>;
  /** Per-leaf evaluation error (keyed by comparator id) — surfaced on the card so 'error' isn't opaque. */
  leafError: Record<string, ConditionError | null>;
}

const noop = () => {};

export const ConditionTreeContext = createContext<ConditionTreeHandlers>({
  onPickOperator: noop,
  onChangeOperand: noop,
  onChangeSample: noop,
  onAddComparator: noop,
  onAddGroup: noop,
  onWrapGroup: noop,
  onWrapNot: noop,
  onSetGroupOp: noop,
  onRemove: noop,
  onUnwrap: noop,
  openOperatorFor: null,
  openInputFor: null,
  setOpenOperator: noop,
  setOpenInput: noop,
  variables: [],
  sampleValues: {},
  testMode: false,
  leafStatus: {},
  leafError: {},
});
