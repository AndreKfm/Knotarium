/**
 * Pure helpers for visual node groups (#14). A group is an editor-only, inert
 * container node type ('group'): like the sticky note it has no ports and is
 * never executed (the backend registers it as a no-op). Membership is carried by
 * each child's React Flow `parentId` (so moving the group moves its children),
 * which schemaMapper persists via `_metadata.parentId`. The group's label and
 * collapsed flag live in `data.properties`; its size in `node.style`.
 *
 * Kept free of React / React Flow so grouping, ungrouping, collapse, and the
 * bounding-box math can be unit-tested with plain node data.
 */
import type { Node as RFNode } from '@xyflow/react';

export const GROUP_TYPE = 'group';

/** Slack around the members' bounding box, and the header strip height. */
export const GROUP_PADDING = 28;
export const GROUP_HEADER_HEIGHT = 34;
/** Size the group shrinks to when collapsed — a compact title chip, not a
 * full-width header bar (so it tucks away and frees the canvas). */
export const GROUP_COLLAPSED_HEIGHT = 30;
export const GROUP_COLLAPSED_WIDTH = 168;
export const DEFAULT_GROUP_LABEL = 'Group';

/**
 * A group's accent colour — used for *categorisation*, not decoration
 * ("blue = ingestion, amber = error handling, …"). Every value is a translucent
 * tint of one base RGB so the body stays see-through (grid + child nodes show
 * through); only the picker dot is solid. Mirrors the sticky-note palette so the
 * two systems feel related, but defaults to indigo rather than amber.
 */
export interface GroupColor {
  id: string;
  label: string;
  swatch: string;       // solid picker dot
  body: string;         // very faint body wash
  headerExpanded: string;
  headerChip: string;   // slightly stronger tint for the collapsed chip
  borderSoft: string;   // unselected (dashed) border
  borderStrong: string; // selected border / chip border
  glowSoft: string;
  glowStrong: string;
  text: string;         // light tint for label, chevron, icons
}

function makeGroupColor(id: string, label: string, r: number, g: number, b: number): GroupColor {
  const c = `${r},${g},${b}`;
  const lighten = (v: number) => Math.round(v + (255 - v) * 0.55);
  return {
    id,
    label,
    swatch: `rgb(${c})`,
    body: `rgba(${c},0.05)`,
    headerExpanded: `rgba(${c},0.08)`,
    headerChip: `rgba(${c},0.14)`,
    borderSoft: `rgba(${c},0.38)`,
    borderStrong: `rgba(${c},0.55)`,
    glowSoft: `rgba(${c},0.16)`,
    glowStrong: `rgba(${c},0.30)`,
    text: `rgb(${lighten(r)},${lighten(g)},${lighten(b)})`,
  };
}

/** The group colour palette. The first entry (indigo) is the default. */
export const GROUP_COLORS: GroupColor[] = [
  makeGroupColor('indigo', 'Indigo', 124, 131, 255),
  makeGroupColor('amber', 'Amber', 232, 179, 57),
  makeGroupColor('green', 'Green', 52, 211, 153),
  makeGroupColor('blue', 'Blue', 59, 130, 246),
  makeGroupColor('pink', 'Pink', 236, 72, 153),
];

export const DEFAULT_GROUP_COLOR_ID = GROUP_COLORS[0].id;

/** Resolve a colour id to its definition, falling back to the default (indigo). */
export function getGroupColor(colorId: string | undefined | null): GroupColor {
  return GROUP_COLORS.find((c) => c.id === colorId) ?? GROUP_COLORS[0];
}

/** Read the group's colour id from its properties (default when unset). */
export function getGroupColorId(node: Pick<RFNode, 'data'>): string {
  const c = (node.data?.properties as Record<string, unknown> | undefined)?.color;
  return typeof c === 'string' ? c : DEFAULT_GROUP_COLOR_ID;
}

/** Fallback node footprint when a node hasn't been measured and has no style size. */
const FALLBACK_NODE_WIDTH = 220;
const FALLBACK_NODE_HEIGHT = 110;

export function isGroupNodeType(type: string | null | undefined): boolean {
  return type === GROUP_TYPE;
}

interface Size { width: number; height: number; }

/** Best-effort node footprint: measured size, else explicit style size, else a fallback. */
export function getNodeSize(node: RFNode): Size {
  const measured = (node as { measured?: Partial<Size> }).measured;
  const styleW = typeof node.style?.width === 'number' ? node.style.width : undefined;
  const styleH = typeof node.style?.height === 'number' ? node.style.height : undefined;
  return {
    width: measured?.width ?? styleW ?? FALLBACK_NODE_WIDTH,
    height: measured?.height ?? styleH ?? FALLBACK_NODE_HEIGHT,
  };
}

export interface Bounds { x: number; y: number; width: number; height: number; }

/** Axis-aligned bounding box covering every node in `members` (in their own coordinate space). */
export function computeGroupBounds(members: RFNode[]): Bounds | null {
  if (members.length === 0) return null;
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const n of members) {
    const { width, height } = getNodeSize(n);
    minX = Math.min(minX, n.position.x);
    minY = Math.min(minY, n.position.y);
    maxX = Math.max(maxX, n.position.x + width);
    maxY = Math.max(maxY, n.position.y + height);
  }
  return { x: minX, y: minY, width: maxX - minX, height: maxY - minY };
}

export function getGroupLabel(node: Pick<RFNode, 'data'>): string {
  const l = (node.data?.properties as Record<string, unknown> | undefined)?.label;
  return typeof l === 'string' && l.length > 0 ? l : DEFAULT_GROUP_LABEL;
}

export function getGroupCollapsed(node: Pick<RFNode, 'data'>): boolean {
  return Boolean((node.data?.properties as Record<string, unknown> | undefined)?.collapsed);
}

/** Child node ids of the group `groupId` (those whose parentId points at it). */
export function childIdsOf(nodes: RFNode[], groupId: string): string[] {
  return nodes.filter((n) => n.parentId === groupId).map((n) => n.id);
}

export interface GroupNodesResult {
  nodes: RFNode[];
  groupId: string;
}

/**
 * Wrap `memberIds` in a new group node. Only top-level members (no existing
 * parent) are grouped — nodes already inside a loop/group are ignored. Returns
 * null if fewer than two eligible members remain (a group of <2 is pointless).
 *
 * Members are reparented (parentId + extent 'parent') and their positions are
 * rebased to be relative to the group's origin, matching how React Flow expects
 * child coordinates. The group node is inserted *before* its children so it
 * paints behind them.
 */
export function groupNodes(nodes: RFNode[], memberIds: string[], groupId: string): GroupNodesResult | null {
  const idSet = new Set(memberIds);
  const members = nodes.filter((n) => idSet.has(n.id) && !n.parentId && !isGroupNodeType(n.type));
  if (members.length < 2) return null;

  const bounds = computeGroupBounds(members);
  if (!bounds) return null;

  const originX = bounds.x - GROUP_PADDING;
  const originY = bounds.y - GROUP_PADDING - GROUP_HEADER_HEIGHT;
  const width = bounds.width + GROUP_PADDING * 2;
  const height = bounds.height + GROUP_PADDING * 2 + GROUP_HEADER_HEIGHT;

  const groupNode: RFNode = {
    id: groupId,
    type: GROUP_TYPE,
    position: { x: originX, y: originY },
    zIndex: 0,
    style: { width, height },
    data: { properties: { label: DEFAULT_GROUP_LABEL, collapsed: false, color: DEFAULT_GROUP_COLOR_ID } },
  };

  const memberSet = new Set(members.map((m) => m.id));
  const reparented = nodes.map((n) =>
    memberSet.has(n.id)
      ? { ...n, parentId: groupId, extent: 'parent' as const, position: { x: n.position.x - originX, y: n.position.y - originY } }
      : n,
  );

  // Group first so it renders behind its children.
  return { nodes: [groupNode, ...reparented], groupId };
}

/**
 * Dissolve the group `groupId`: remove the group node and restore each child to
 * an absolute (top-level) position with its parenting cleared. Nodes that aren't
 * children of the group are returned untouched.
 */
export function ungroupNodes(nodes: RFNode[], groupId: string): RFNode[] {
  const group = nodes.find((n) => n.id === groupId && isGroupNodeType(n.type));
  if (!group) return nodes;
  return nodes
    .filter((n) => n.id !== groupId)
    .map((n) =>
      n.parentId === groupId
        ? {
            ...n,
            parentId: undefined,
            extent: undefined,
            hidden: false,
            position: { x: n.position.x + group.position.x, y: n.position.y + group.position.y },
          }
        : n,
    );
}

/**
 * Toggle the collapsed state of group `groupId`. Collapsing hides its children
 * and snaps the group to a compact title chip ({@link GROUP_COLLAPSED_WIDTH} ×
 * {@link GROUP_COLLAPSED_HEIGHT}) anchored at the same top-left, remembering the
 * expanded `{width,height}` in `properties`. Expanding restores that exact
 * remembered size (it isn't recomputed) and shows the children again.
 */
export function toggleGroupCollapsed(nodes: RFNode[], groupId: string): RFNode[] {
  const group = nodes.find((n) => n.id === groupId && isGroupNodeType(n.type));
  if (!group) return nodes;
  const collapsing = !getGroupCollapsed(group);
  const currentWidth = typeof group.style?.width === 'number' ? group.style.width : undefined;
  const currentHeight = typeof group.style?.height === 'number' ? group.style.height : undefined;

  return nodes.map((n) => {
    if (n.id === groupId) {
      const props = (n.data?.properties as Record<string, unknown>) || {};
      if (collapsing) {
        return {
          ...n,
          style: { ...n.style, width: GROUP_COLLAPSED_WIDTH, height: GROUP_COLLAPSED_HEIGHT },
          data: {
            ...n.data,
            properties: {
              ...props,
              collapsed: true,
              // Remember the live size so re-expand restores it verbatim.
              expandedWidth: currentWidth ?? props.expandedWidth,
              expandedHeight: currentHeight ?? props.expandedHeight,
            },
          },
        };
      }
      const restoredWidth = typeof props.expandedWidth === 'number' ? props.expandedWidth : currentWidth;
      const restoredHeight = typeof props.expandedHeight === 'number' ? props.expandedHeight : currentHeight;
      return {
        ...n,
        style: {
          ...n.style,
          ...(restoredWidth != null ? { width: restoredWidth } : {}),
          ...(restoredHeight != null ? { height: restoredHeight } : {}),
        },
        data: { ...n.data, properties: { ...props, collapsed: false } },
      };
    }
    if (n.parentId === groupId) {
      return { ...n, hidden: collapsing };
    }
    return n;
  });
}

/**
 * Shift every child of group `groupId` by `(dx,dy)` in the group's local
 * coordinate space. Used to keep contained nodes visually anchored when a
 * top/left resize handle moves the group's origin: the frame resizes *around*
 * the nodes instead of dragging them along. A no-op (same array) when nothing
 * moves.
 */
export function offsetGroupChildren(nodes: RFNode[], groupId: string, dx: number, dy: number): RFNode[] {
  if (dx === 0 && dy === 0) return nodes;
  return nodes.map((n) =>
    n.parentId === groupId
      ? { ...n, position: { x: n.position.x + dx, y: n.position.y + dy } }
      : n,
  );
}

/**
 * After loading a workflow, re-derive each child's `hidden` flag from its group's
 * persisted collapsed flag (the runtime `hidden` flag itself isn't persisted).
 * Idempotent — safe to run on every load.
 */
export function applyGroupCollapseOnLoad(nodes: RFNode[]): RFNode[] {
  const collapsedGroupIds = new Set(
    nodes.filter((n) => isGroupNodeType(n.type) && getGroupCollapsed(n)).map((n) => n.id),
  );
  if (collapsedGroupIds.size === 0) return nodes;
  return nodes.map((n) =>
    n.parentId && collapsedGroupIds.has(n.parentId) ? { ...n, hidden: true } : n,
  );
}

/**
 * The expanded group whose bounds contain `point` (absolute flow coords), or
 * undefined. Collapsed groups are skipped — you can't drop a node into a strip
 * that has no body. `excludeId` skips a node (e.g. the one being dragged).
 */
export function findContainingGroupNode(
  nodes: RFNode[],
  point: { x: number; y: number },
  excludeId?: string,
): RFNode | undefined {
  return nodes.find((n) => {
    if (!isGroupNodeType(n.type) || n.id === excludeId || getGroupCollapsed(n)) return false;
    const width = typeof n.style?.width === 'number' ? n.style.width : 0;
    const height = typeof n.style?.height === 'number' ? n.style.height : 0;
    return (
      point.x >= n.position.x &&
      point.x <= n.position.x + width &&
      point.y >= n.position.y &&
      point.y <= n.position.y + height
    );
  });
}

/** Immutably set the label of group `groupId`. */
export function applyGroupLabel(nodes: RFNode[], groupId: string, label: string): RFNode[] {
  return nodes.map((n) =>
    n.id === groupId
      ? { ...n, data: { ...n.data, properties: { ...(n.data?.properties as object), label } } }
      : n,
  );
}

/** Immutably set the accent colour of group `groupId`. */
export function applyGroupColor(nodes: RFNode[], groupId: string, colorId: string): RFNode[] {
  return nodes.map((n) =>
    n.id === groupId
      ? { ...n, data: { ...n.data, properties: { ...(n.data?.properties as object), color: colorId } } }
      : n,
  );
}

/** Count of the group's *direct* children (nodes whose parentId points at it). */
export function directChildCount(nodes: RFNode[], groupId: string): number {
  return childIdsOf(nodes, groupId).length;
}
