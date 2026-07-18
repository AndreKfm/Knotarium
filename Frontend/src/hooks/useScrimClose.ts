// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect } from 'react';

/**
 * Shared "dismiss a modal by its backdrop" behavior.
 *
 * Returns an `onMouseDown` handler to put on the scrim/backdrop element, and registers an
 * Escape-to-close listener while `enabled`.
 *
 * Why `onMouseDown` (on the scrim itself) rather than the tempting `onClick={onClose}`:
 * a DOM `click` fires on the nearest common ancestor of where the mouse went *down* and where
 * it came *up*. So if you start a text selection inside the dialog (a code editor, a textarea)
 * and release the button out over the backdrop, the click lands on the backdrop and the dialog
 * closes mid-selection. Keying off `mousedown` on the scrim closes only when the press itself
 * landed on the backdrop — a genuine click-away — never on a selection that merely ends there.
 *
 * @param onClose  called to dismiss the dialog.
 * @param enabled  set false to suspend both Esc and backdrop dismissal (e.g. while a request is
 *                 in flight); defaults to true.
 */
export function useScrimClose(onClose: () => void, enabled = true): (e: React.MouseEvent) => void {
  useEffect(() => {
    if (!enabled) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    };
    // Capture phase so a dialog on top wins over any background Esc handlers.
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [enabled, onClose]);

  return useCallback(
    (e: React.MouseEvent) => {
      if (enabled && e.target === e.currentTarget) {
        onClose();
      }
    },
    [enabled, onClose],
  );
}
