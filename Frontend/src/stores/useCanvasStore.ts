import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

export const canvasPaletteStorageKey = 'knotarium:canvas-palette';
export const maxRecentNodeIds = 5;

interface CanvasPaletteState {
  pinnedNodeIds: string[];
  recentNodeIds: string[];
  togglePinNode: (nodeId: string) => void;
  addRecentNode: (nodeId: string) => void;
  resetPaletteState: () => void;
}

const initialPaletteState = {
  pinnedNodeIds: [],
  recentNodeIds: [],
};

export const useCanvasStore = create<CanvasPaletteState>()(
  persist(
    (set) => ({
      ...initialPaletteState,
      togglePinNode: (nodeId: string) => {
        set((state) => {
          const isPinned = state.pinnedNodeIds.includes(nodeId);

          return {
            pinnedNodeIds: isPinned
              ? state.pinnedNodeIds.filter((currentNodeId) => currentNodeId !== nodeId)
              : [...state.pinnedNodeIds, nodeId],
          };
        });
      },
      addRecentNode: (nodeId: string) => {
        set((state) => {
          const nextRecentNodeIds = [...state.recentNodeIds.filter((currentNodeId) => currentNodeId !== nodeId), nodeId];

          return {
            recentNodeIds: nextRecentNodeIds.slice(-maxRecentNodeIds),
          };
        });
      },
      resetPaletteState: () => {
        set(initialPaletteState);
      },
    }),
    {
      name: canvasPaletteStorageKey,
      storage: createJSONStorage(() => localStorage),
      partialize: (state) => ({
        pinnedNodeIds: state.pinnedNodeIds,
        recentNodeIds: state.recentNodeIds,
      }),
    },
  ),
);