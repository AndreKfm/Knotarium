import { create } from 'zustand';

// Bridges a canvas gesture (double-click an Inline Code node) to the editor modal, which
// lives in the properties panel's ManifestForm. The canvas requests an open by node id;
// ManifestForm (rendered for the selected node) consumes the request and opens the modal.
interface InlineCodeEditorState {
  requestNodeId: string | null;
  requestOpen: (nodeId: string) => void;
  clearRequest: () => void;
}

export const useInlineCodeEditorStore = create<InlineCodeEditorState>((set) => ({
  requestNodeId: null,
  requestOpen: (nodeId: string) => set({ requestNodeId: nodeId }),
  clearRequest: () => set({ requestNodeId: null }),
}));
