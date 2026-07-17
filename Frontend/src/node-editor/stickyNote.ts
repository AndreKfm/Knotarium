// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

/**
 * Pure helpers for sticky-note annotation nodes (#13). A sticky note is an
 * editor-only, inert node type ('stickyNote'): it has no ports and is never
 * executed (the backend registers it as a no-op so it round-trips through
 * save/version/restore like any other node). Its text + colour live in
 * `data.properties` and its size in `node.style`, which schemaMapper persists
 * via the existing `_metadata` channel.
 *
 * Kept free of React / React Flow so the construction and immutable update rules
 * can be unit-tested with plain node data.
 */
import type { Node as RFNode } from '@xyflow/react';

export const STICKY_NOTE_TYPE = 'stickyNote';

export const STICKY_NOTE_DEFAULT_SIZE = { width: 220, height: 150 } as const;

/**
 * A selectable note colour. Every visual value is derived from one base swatch
 * colour `C = (r,g,b)` so the note reads as a *translucent wash of C over the
 * dark canvas* rather than a solid fill — the canvas grid and theme show
 * through. See the design handoff "Note & Group Refinement".
 *
 * - `swatch` — the solid picker dot (the only opaque use of the colour).
 * - `bg`     — translucent gradient wash for the card body.
 * - `border` — translucent hairline.
 * - `glow`   — rgba used in the card's coloured drop-shadow.
 * - `text`   — a light, readable tint of C for the note text/placeholder.
 */
export interface StickyNoteColor {
  id: string;
  label: string;
  swatch: string;
  bg: string;
  border: string;
  glow: string;
  text: string;
}

/** Derive a note colour's translucent style set from its base RGB swatch. */
function makeNoteColor(id: string, label: string, r: number, g: number, b: number): StickyNoteColor {
  const c = `${r},${g},${b}`;
  // Lighten the swatch toward white (~60%) so typed text stays readable on the
  // dark wash; the textarea renders the placeholder dimmed from this same ink.
  const lighten = (v: number) => Math.round(v + (255 - v) * 0.6);
  return {
    id,
    label,
    swatch: `rgb(${c})`,
    bg: `linear-gradient(165deg, rgba(${c},.16), rgba(${c},.07) 60%, rgba(${c},.05))`,
    border: `rgba(${c},.42)`,
    glow: `rgba(${c},.5)`,
    text: `rgb(${lighten(r)},${lighten(g)},${lighten(b)})`,
  };
}

/** The note colour palette. The first entry is the default. */
export const STICKY_NOTE_COLORS: StickyNoteColor[] = [
  makeNoteColor('amber', 'Amber', 232, 179, 57),
  makeNoteColor('green', 'Green', 52, 211, 153),
  makeNoteColor('blue',  'Blue',  59, 130, 246),
  makeNoteColor('pink',  'Pink',  236, 72, 153),
  makeNoteColor('slate', 'Slate', 122, 133, 149),
];

export const DEFAULT_STICKY_NOTE_COLOR_ID = STICKY_NOTE_COLORS[0].id;

export function isStickyNote(type: string | null | undefined): boolean {
  return type === STICKY_NOTE_TYPE;
}

/** Resolve a colour id to its definition, falling back to the default palette entry. */
export function getStickyNoteColor(colorId: string | undefined | null): StickyNoteColor {
  return STICKY_NOTE_COLORS.find((c) => c.id === colorId) ?? STICKY_NOTE_COLORS[0];
}

export interface CreateStickyNoteOptions {
  id: string;
  position: { x: number; y: number };
  text?: string;
  colorId?: string;
  width?: number;
  height?: number;
}

/** Build a fresh sticky-note React Flow node. */
export function createStickyNoteNode(options: CreateStickyNoteOptions): RFNode {
  const { id, position, text = '', colorId = DEFAULT_STICKY_NOTE_COLOR_ID } = options;
  return {
    id,
    type: STICKY_NOTE_TYPE,
    position,
    // Sit behind real nodes so a note used as a backdrop never blocks clicks on them.
    zIndex: 0,
    style: {
      width: options.width ?? STICKY_NOTE_DEFAULT_SIZE.width,
      height: options.height ?? STICKY_NOTE_DEFAULT_SIZE.height,
    },
    data: {
      properties: { text, color: colorId },
    },
  };
}

/** Read the note's text from its properties (empty string when unset). */
export function getStickyNoteText(node: Pick<RFNode, 'data'>): string {
  const t = (node.data?.properties as Record<string, unknown> | undefined)?.text;
  return typeof t === 'string' ? t : '';
}

/** Read the note's colour id from its properties (default when unset). */
export function getStickyNoteColorId(node: Pick<RFNode, 'data'>): string {
  const c = (node.data?.properties as Record<string, unknown> | undefined)?.color;
  return typeof c === 'string' ? c : DEFAULT_STICKY_NOTE_COLOR_ID;
}

/** Immutably set the text of the sticky note `id`, leaving other nodes untouched. */
export function applyStickyNoteText(nodes: RFNode[], id: string, text: string): RFNode[] {
  return nodes.map((n) =>
    n.id === id
      ? { ...n, data: { ...n.data, properties: { ...(n.data?.properties as object), text, color: getStickyNoteColorId(n) } } }
      : n,
  );
}

/** Immutably set the colour of the sticky note `id`, leaving other nodes untouched. */
export function applyStickyNoteColor(nodes: RFNode[], id: string, colorId: string): RFNode[] {
  return nodes.map((n) =>
    n.id === id
      ? { ...n, data: { ...n.data, properties: { ...(n.data?.properties as object), text: getStickyNoteText(n), color: colorId } } }
      : n,
  );
}
