// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import {
  DRAFT_MODE,
  editorModeReducer,
  isReadOnly,
  isEditingDisabled,
  type EditorMode,
} from './editorMode';

describe('editorMode reducer', () => {
  it('starts in draft and reports it editable', () => {
    expect(DRAFT_MODE.kind).toBe('draft');
    expect(isReadOnly(DRAFT_MODE)).toBe(false);
    expect(isEditingDisabled(DRAFT_MODE)).toBe(false);
  });

  it('enters preview from draft and marks it read-only', () => {
    const next = editorModeReducer(DRAFT_MODE, { type: 'openPreview', versionId: 'v9' });
    expect(next).toEqual({ kind: 'preview', versionId: 'v9' });
    expect(isReadOnly(next)).toBe(true);
    expect(isEditingDisabled(next)).toBe(true);
  });

  it('enters diff from draft', () => {
    const next = editorModeReducer(DRAFT_MODE, { type: 'openDiff', leftVersionId: 'a', rightVersionId: 'b' });
    expect(next).toEqual({ kind: 'diff', leftVersionId: 'a', rightVersionId: 'b' });
    expect(isReadOnly(next)).toBe(true);
  });

  it('exit always returns to draft from any mode', () => {
    const preview: EditorMode = { kind: 'preview', versionId: 'v1' };
    const diff: EditorMode = { kind: 'diff', leftVersionId: 'a', rightVersionId: 'b' };
    expect(editorModeReducer(preview, { type: 'exit' })).toEqual(DRAFT_MODE);
    expect(editorModeReducer(diff, { type: 'exit' })).toEqual(DRAFT_MODE);
  });

  it('can switch preview target while already previewing (no nested modes)', () => {
    const preview: EditorMode = { kind: 'preview', versionId: 'v1' };
    const next = editorModeReducer(preview, { type: 'openPreview', versionId: 'v2' });
    expect(next).toEqual({ kind: 'preview', versionId: 'v2' });
  });
});
