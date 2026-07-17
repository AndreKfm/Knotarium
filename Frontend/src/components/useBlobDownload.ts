// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback } from 'react';

/**
 * Triggers a browser download of a Blob under a chosen filename. Shared by the bundle and template
 * exporters so the object-URL lifecycle (create → click → revoke) lives in exactly one place.
 */
export function useBlobDownload() {
  return useCallback((blob: Blob, filename: string) => {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  }, []);
}
