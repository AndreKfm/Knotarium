// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { create } from 'zustand';

// Bridges a "Grant this path" gesture from a failed run (the ExecutionSidebar CTA on a File Access
// denial) to the File Access settings page, which pre-fills the path as a new grant row. The sidebar
// requests a grant by path; FileAccessSetting consumes it on mount and clears it. Mirrors
// useSubflowOpenStore — App performs the actual view switch to 'settings'.
interface PendingFileAccessGrantState {
  pendingPath: string | null;
  requestGrant: (path: string) => void;
  clear: () => void;
}

export const usePendingFileAccessGrantStore = create<PendingFileAccessGrantState>((set) => ({
  pendingPath: null,
  requestGrant: (path: string) => set({ pendingPath: path }),
  clear: () => set({ pendingPath: null }),
}));
