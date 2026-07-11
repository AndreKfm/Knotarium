import { create } from 'zustand';

// Bridges a canvas gesture (double-click a Condition node) to the full-screen logic editor,
// which lives in the properties panel's ConditionLogicField. The canvas requests an open by
// node id; ConditionLogicField (rendered for the selected node) consumes the request and opens
// the editor. Mirrors useInlineCodeEditorStore / useSubflowOpenStore.
interface ConditionEditorOpenState {
  requestNodeId: string | null;
  requestOpen: (nodeId: string) => void;
  clearRequest: () => void;
}

export const useConditionEditorOpenStore = create<ConditionEditorOpenState>((set) => ({
  requestNodeId: null,
  requestOpen: (nodeId: string) => set({ requestNodeId: nodeId }),
  clearRequest: () => set({ requestNodeId: null }),
}));
