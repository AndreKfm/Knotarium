import type { ReactNode } from 'react';
import {
  AlertTriangle, Bell, Calendar, Code2, FileText, Forward, Hourglass, Megaphone,
  FileDiff, MousePointerClick, Play, Puzzle, RadioTower, RefreshCw, Router, Send, ShieldCheck, Sparkles, Split, Square, Variable, Zap,
} from 'lucide-react';

// Node palette icons: each node type gets a characteristic glyph; the tile is tinted by category, so
// shape + colour identify a node before you read its name. Glyphs marked "sample" are the hand-drawn set
// from the design mockup; the rest use matching thin-stroke line icons. Unknown / imported types fall back
// to a neutral glyph that is still category-tinted, so a third-party node is never invisible.

export type NodeCategoryKey = 'Trigger' | 'Logic' | 'Data' | 'Network' | 'Ai' | 'Utility';

export const NODE_CATEGORY_STYLE: Record<NodeCategoryKey, { color: string; bg: string; border: string }> = {
  Trigger: { color: '#34d399', bg: 'rgba(52,211,153,0.12)', border: 'rgba(52,211,153,0.32)' },
  Logic: { color: '#a99bff', bg: 'rgba(124,108,240,0.14)', border: 'rgba(124,108,240,0.34)' },
  Data: { color: '#f0b429', bg: 'rgba(240,180,41,0.12)', border: 'rgba(240,180,41,0.32)' },
  Network: { color: '#22d3ee', bg: 'rgba(34,211,238,0.12)', border: 'rgba(34,211,238,0.32)' },
  Ai: { color: '#f472b6', bg: 'rgba(244,114,182,0.12)', border: 'rgba(244,114,182,0.32)' },
  Utility: { color: '#94a3b8', bg: 'rgba(148,163,184,0.12)', border: 'rgba(148,163,184,0.30)' },
};

function normalizeCategoryKey(category: string): NodeCategoryKey {
  return (['Trigger', 'Logic', 'Data', 'Network', 'Ai', 'Utility'] as const).includes(category as NodeCategoryKey)
    ? (category as NodeCategoryKey)
    : 'Utility';
}

// Hand-drawn sample glyphs (24×24, stroke-based) from the design mockup, keyed by a glyph name.
const SAMPLE_GLYPH: Record<string, ReactNode> = {
  webhook: (<><circle cx="12" cy="6" r="2.6" /><circle cx="6" cy="18" r="2.6" /><circle cx="18" cy="18" r="2.6" /><path d="M12 8.6l-3.6 6.8M12 8.6l3.6 6.8M8.6 18h6.8" /></>),
  condition: (<><circle cx="6" cy="6" r="2.4" /><circle cx="6" cy="18" r="2.4" /><circle cx="18" cy="12" r="2.4" /><path d="M8.4 6H11a4 4 0 0 1 4 4v.6M8.4 18H11a4 4 0 0 0 4-4v-.6" /></>),
  delay: (<><circle cx="12" cy="13" r="7.5" /><path d="M12 9.5v3.5l2.5 1.8" /><path d="M9 3h6" /></>),
  forloop: (<><path d="M17 3l3.2 3.2L17 9.4" /><path d="M3.8 11V9.2a4 4 0 0 1 4-4h12.4" /><path d="M7 21l-3.2-3.2L7 14.6" /><path d="M20.2 13v1.8a4 4 0 0 1-4 4H3.8" /></>),
  join: (<><path d="M6 3v4.5a6 6 0 0 0 6 6 6 6 0 0 0 6-6V3" /><path d="M12 13.5V21" /><path d="M9.5 18.5L12 21l2.5-2.5" /></>),
  parallel: (<><path d="M3.5 6.5h11M3.5 12h11M3.5 17.5h11" /><path d="M17.5 4.2l3 2.3-3 2.3M17.5 15.2l3 2.3-3 2.3" /></>),
  subflow: (<><rect x="3" y="3" width="18" height="18" rx="3.5" /><rect x="7.5" y="7.5" width="9" height="9" rx="2" /></>),
  switch: (<><circle cx="5.5" cy="12" r="2.2" /><circle cx="18.5" cy="5.5" r="2.2" /><circle cx="18.5" cy="12" r="2.2" /><circle cx="18.5" cy="18.5" r="2.2" /><path d="M7.7 12h8.6M7.7 11L16 6.4M7.7 13L16 17.6" /></>),
  merge: (<><circle cx="9" cy="12" r="5.5" /><circle cx="15" cy="12" r="5.5" /></>),
  database: (<><ellipse cx="12" cy="5.5" rx="7.5" ry="2.8" /><path d="M4.5 5.5v6c0 1.6 3.4 2.8 7.5 2.8s7.5-1.2 7.5-2.8v-6M4.5 11.5v6c0 1.6 3.4 2.8 7.5 2.8s7.5-1.2 7.5-2.8v-6" /></>),
  transform: (<><path d="M4 7.5h11l-3-3M4 7.5l3 3M20 16.5H9l3 3M20 16.5l-3-3" /></>),
  cloud: (<><path d="M7 18.5a4.2 4.2 0 0 1 0-8.4 5.2 5.2 0 0 1 10-1.4 3.7 3.7 0 0 1 1 7.3" /><path d="M7 18.5h10" /></>),
  globe: (<><circle cx="12" cy="12" r="8.5" /><path d="M3.5 12h17M12 3.5c2.6 2.8 2.6 14.2 0 17M12 3.5c-2.6 2.8-2.6 14.2 0 17" /></>),
  spec: (<><path d="M9 4H7a2 2 0 0 0-2 2v3.2a2 2 0 0 1-1.4 1.9L3 11.4l.6.3A2 2 0 0 1 5 13.6V17a2 2 0 0 0 2 2h2M15 4h2a2 2 0 0 1 2 2v3.2a2 2 0 0 0 1.4 1.9l.6.3-.6.3a2 2 0 0 0-1.4 1.9V17a2 2 0 0 1-2 2h-2" /></>),
};

// Node type id → sample glyph name (the mockup's hand-drawn set).
const NODE_TO_SAMPLE: Record<string, string> = {
  webhookTrigger: 'webhook',
  condition: 'condition',
  delay: 'delay',
  forLoop: 'forloop',
  join: 'join',
  parallelForEach: 'parallel',
  subflow: 'subflow',
  switch: 'switch',
  merge: 'merge',
  resourcePicker: 'database',
  transform: 'transform',
  httpRequest: 'globe',
};

// Node type id → lucide line icon (same thin-stroke language) for types the sample set doesn't cover.
const NODE_TO_LUCIDE: Record<string, typeof Play> = {
  scheduler: Calendar,
  start: Play,
  end: Square,
  errorTrigger: AlertTriangle,
  manualTrigger: MousePointerClick,
  pollingTrigger: RefreshCw,
  actionTrigger: Zap,
  eventTrigger: RadioTower,
  fireAction: Send,
  setEvent: Megaphone,
  externalDevice: Router,
  redirectResource: Forward,
  waitForEvent: Hourglass,
  inlineCode: Code2,
  log: FileText,
  setVariable: Variable,
  setVariables: Variable,
  sendNotification: Bell,
  aiPrompt: Sparkles,
  aiRouter: Split,
  aiVerify: ShieldCheck,
  aiDiff: FileDiff,
};

function resolveGlyph(nodeId: string, glyphSize: number): ReactNode {
  const sampleName = NODE_TO_SAMPLE[nodeId] ?? (nodeId.startsWith('openapi.') ? 'spec' : undefined);
  if (sampleName && SAMPLE_GLYPH[sampleName]) {
    return (
      <svg width={glyphSize} height={glyphSize} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
        {SAMPLE_GLYPH[sampleName]}
      </svg>
    );
  }
  const LucideIcon = NODE_TO_LUCIDE[nodeId] ?? Puzzle;
  return <LucideIcon size={glyphSize} />;
}

/**
 * A node's palette/canvas icon: a category-tinted rounded tile holding the type's glyph. Pass the node
 * package id and its normalized palette category. Falls back to a neutral (Puzzle) glyph for unknown types.
 */
export function NodeIcon({
  nodeId,
  category,
  size = 30,
  glyphSize = 16,
}: {
  nodeId: string;
  category: string;
  size?: number;
  glyphSize?: number;
}) {
  const style = NODE_CATEGORY_STYLE[normalizeCategoryKey(category)];
  return (
    <span
      aria-hidden="true"
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        width: `${size}px`,
        height: `${size}px`,
        borderRadius: `${Math.round(size * 0.32)}px`,
        background: style.bg,
        border: `1px solid ${style.border}`,
        color: style.color,
        flexShrink: 0,
      }}
    >
      {resolveGlyph(nodeId, glyphSize)}
    </span>
  );
}
