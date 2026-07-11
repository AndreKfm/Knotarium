import { create } from 'zustand';
import type { SignalFieldGroup } from '../node-editor/signalFieldBinding';

/**
 * The inbound-signal field groups scoped to the CURRENTLY-selected node — the action(s) whose signal can
 * reach it, and their payload keys. Populated by the Canvas (which owns nodes/edges + the fetched schema)
 * and read by the node's editors:
 *  - the properties panel renders them as click-to-copy chips, and
 *  - the Condition operand reference picker merges them in, so a field becomes a proper resolving `ref`
 *    operand rather than a literal `{{ }}` string.
 *
 * Bound per node (one selection at a time), so the fields stay tied to the action INSTANCE that feeds the
 * node instead of leaking into the canvas-wide variable store.
 */
interface SignalFieldState {
  nodeId: string | null;
  groups: SignalFieldGroup[];
  setSignalFields: (nodeId: string | null, groups: SignalFieldGroup[]) => void;
}

export const useSignalFieldStore = create<SignalFieldState>((set) => ({
  nodeId: null,
  groups: [],
  setSignalFields: (nodeId, groups) => set({ nodeId, groups }),
}));

// Stable empty reference so selectors don't return a fresh array each render (useSyncExternalStore footgun).
const EMPTY: SignalFieldGroup[] = [];

/** The groups for `nodeId` if it's the one currently published to the store, else a stable empty array. */
export function signalGroupsFor(state: SignalFieldState, nodeId: string | null | undefined): SignalFieldGroup[] {
  return nodeId != null && state.nodeId === nodeId ? state.groups : EMPTY;
}
