export interface Shortcut {
  keys: string;
  description: string;
}
export interface ShortcutGroup {
  title: string;
  items: Shortcut[];
}

// Single source of truth for the editor's keyboard map, rendered by
// KeyboardShortcutsHelp. Kept in a data-only module so a test can assert it
// stays in sync with the handlers wired in Canvas.tsx.
//
// Use the `Mod` token for the primary modifier; it renders as ⌘ on macOS and
// Ctrl everywhere else (see formatShortcutKeys / isMacPlatform below).
export const SHORTCUT_GROUPS: ShortcutGroup[] = [
  {
    title: 'Navigation',
    items: [
      { keys: 'Mod + F  ·  Mod + K', description: 'Search / jump to a node' },
      { keys: 'Mod + Shift + H', description: 'Toggle the version history panel' },
      { keys: '?', description: 'Toggle this shortcut help' },
      { keys: 'Esc', description: 'Clear pins / cancel a pending connection' },
    ],
  },
  {
    title: 'Editing',
    items: [
      { keys: 'Mod + Z', description: 'Undo' },
      { keys: 'Mod + Shift + Z  ·  Mod + Y', description: 'Redo' },
      { keys: 'Mod + C', description: 'Copy selection' },
      { keys: 'Mod + V', description: 'Paste' },
      { keys: 'Mod + D', description: 'Duplicate selection' },
      { keys: 'Mod + A', description: 'Select all nodes' },
      { keys: 'Delete · Backspace', description: 'Delete selected nodes / edges' },
    ],
  },
  {
    title: 'Layout (toolbar)',
    items: [
      { keys: 'Tidy', description: 'Auto-arrange the graph left → right' },
      { keys: 'Grid', description: 'Toggle snap-to-grid' },
      { keys: 'Align / Distribute', description: 'Appear when 2+ (distribute: 3+) nodes are selected' },
    ],
  },
  {
    title: 'Nodes',
    items: [
      { keys: 'Double-click subflow  ·  ↗', description: 'Open the referenced workflow' },
      { keys: 'Double-click Inline Code', description: 'Open the code editor' },
      { keys: 'Drag onto a wire', description: 'Insert the node on that connection' },
    ],
  },
];

/** Narrow shape of the bits of `navigator` we read; keeps the detector testable. */
interface PlatformNavigator {
  platform?: string;
  userAgent?: string;
  userAgentData?: { platform?: string };
}

/**
 * True on macOS. Prefers the modern `userAgentData.platform`, falls back to the
 * (deprecated but universal) `navigator.platform`, then the UA string. Returns
 * false in non-browser contexts. Accepts an injected navigator for testing.
 */
export function isMacPlatform(
  nav: PlatformNavigator | undefined = typeof navigator !== 'undefined' ? navigator : undefined,
): boolean {
  if (!nav) return false;
  const platform = (nav.userAgentData?.platform || nav.platform || '').toLowerCase();
  if (platform) return platform.includes('mac');
  return /mac/i.test(nav.userAgent || '');
}

/** Render a shortcut's `keys`, substituting the `Mod` token for the platform modifier. */
export function formatShortcutKeys(keys: string, isMac: boolean): string {
  return keys.replaceAll('Mod', isMac ? '⌘' : 'Ctrl');
}
