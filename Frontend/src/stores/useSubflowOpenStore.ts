// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { create } from 'zustand';

// Bridges a subflow-node "open" gesture (the drill-down icon on the card, or a
// double-click) to Canvas, which performs the actual navigation (it owns the
// save-before-open + onOpenSubflow plumbing). The card requests an open by node
// id; Canvas consumes the request and clears it. Mirrors useInlineCodeEditorStore.
interface SubflowOpenState {
  requestNodeId: string | null;
  requestOpen: (nodeId: string) => void;
  clearRequest: () => void;
}

export const useSubflowOpenStore = create<SubflowOpenState>((set) => ({
  requestNodeId: null,
  requestOpen: (nodeId: string) => set({ requestNodeId: nodeId }),
  clearRequest: () => set({ requestNodeId: null }),
}));
