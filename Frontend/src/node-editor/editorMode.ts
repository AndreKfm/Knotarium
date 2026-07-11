// Pure, framework-agnostic editor-mode state machine (plan §7.3).
//
// The workflow editor canvas is normally in `Draft` mode: the user's live,
// editable working graph. Previewing a published version must NOT mutate that
// draft — instead the editor transitions into a read-only mode while the draft
// is held aside as a snapshot and restored verbatim on exit.
//
//   EditorMode = Draft
//              | PublishedPreview(versionId)
//              | Diff(leftVersionId, rightVersionId)
//
// This module owns only the *mode* transitions and the invariants between them
// (you can only enter preview/diff from Draft; exiting always returns to Draft).
// It deliberately holds no React or canvas state — the component reads `mode.kind`
// to decide whether editing/autosave/publish are disabled, and keeps the actual
// draft snapshot next to it. Keeping the logic here makes the transitions unit
// testable in isolation.

export type EditorMode =
  | { kind: 'draft' }
  | { kind: 'preview'; versionId: string }
  | { kind: 'diff'; leftVersionId: string; rightVersionId: string };

export const DRAFT_MODE: EditorMode = { kind: 'draft' };

/** True while the canvas shows a read-only snapshot (preview or diff) rather than the live draft. */
export function isReadOnly(mode: EditorMode): boolean {
  return mode.kind !== 'draft';
}

/** True when editing / autosave / publish must be disabled for the current mode. */
export function isEditingDisabled(mode: EditorMode): boolean {
  return isReadOnly(mode);
}

export type EditorModeAction =
  | { type: 'openPreview'; versionId: string }
  | { type: 'openDiff'; leftVersionId: string; rightVersionId: string }
  | { type: 'exit' };

/**
 * Reduce an action against the current mode. Entering preview or diff is only
 * permitted from `draft` (and, as a convenience, switching directly between two
 * preview/diff targets) — you can never nest read-only modes ambiguously. `exit`
 * always returns to `draft`. Unknown / illegal transitions return the current
 * mode unchanged so callers never crash on a stray action (e.g. a remote-activation
 * event arriving while already previewing).
 */
export function editorModeReducer(mode: EditorMode, action: EditorModeAction): EditorMode {
  switch (action.type) {
    case 'openPreview':
      return { kind: 'preview', versionId: action.versionId };
    case 'openDiff':
      return {
        kind: 'diff',
        leftVersionId: action.leftVersionId,
        rightVersionId: action.rightVersionId,
      };
    case 'exit':
      return DRAFT_MODE;
    default:
      return mode;
  }
}
