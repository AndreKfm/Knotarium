import { describe, it, expect } from 'vitest';
import type { Node as RFNode } from '@xyflow/react';
import {
  groupNodes,
  ungroupNodes,
  toggleGroupCollapsed,
  offsetGroupChildren,
  applyGroupCollapseOnLoad,
  applyGroupLabel,
  applyGroupColor,
  getGroupColor,
  getGroupColorId,
  directChildCount,
  computeGroupBounds,
  getGroupCollapsed,
  getGroupLabel,
  childIdsOf,
  isGroupNodeType,
  GROUP_TYPE,
  GROUP_COLLAPSED_HEIGHT,
  GROUP_COLLAPSED_WIDTH,
  GROUP_COLORS,
  DEFAULT_GROUP_COLOR_ID,
  DEFAULT_GROUP_LABEL,
} from './nodeGroup';

function node(id: string, x: number, y: number, extra: Partial<RFNode> = {}): RFNode {
  return { id, type: 'log', position: { x, y }, data: {}, style: { width: 100, height: 100 }, ...extra };
}

describe('computeGroupBounds', () => {
  it('covers all members including their footprints', () => {
    const b = computeGroupBounds([node('a', 0, 0), node('b', 200, 100)]);
    expect(b).toEqual({ x: 0, y: 0, width: 300, height: 200 });
  });
  it('returns null for no members', () => {
    expect(computeGroupBounds([])).toBeNull();
  });
});

describe('groupNodes', () => {
  it('creates a group wrapping ≥2 top-level members and reparents them', () => {
    const nodes = [node('a', 100, 100), node('b', 300, 200), node('c', 1000, 1000)];
    const res = groupNodes(nodes, ['a', 'b'], 'group-1');
    expect(res).not.toBeNull();
    const group = res!.nodes.find((n) => n.id === 'group-1')!;

    expect(isGroupNodeType(group.type)).toBe(true);
    // Group renders first (behind its children).
    expect(res!.nodes[0].id).toBe('group-1');
    expect(getGroupCollapsed(group)).toBe(false);
    expect(getGroupLabel(group)).toBe(DEFAULT_GROUP_LABEL);

    const a = res!.nodes.find((n) => n.id === 'a')!;
    expect(a.parentId).toBe('group-1');
    expect(a.extent).toBe('parent');
    // Child position is now relative to the group origin (was absolute 100,100).
    expect(a.position.x).toBe(100 - group.position.x);
    expect(a.position.y).toBe(100 - group.position.y);

    // Untouched node keeps its identity and no parent.
    expect(res!.nodes.find((n) => n.id === 'c')!.parentId).toBeUndefined();
    expect(childIdsOf(res!.nodes, 'group-1').sort()).toEqual(['a', 'b']);
  });

  it('refuses to group fewer than two eligible members', () => {
    const nodes = [node('a', 0, 0), node('b', 10, 10, { parentId: 'other' })];
    // b is already parented, leaving only a → not enough.
    expect(groupNodes(nodes, ['a', 'b'], 'g')).toBeNull();
  });

  it('ignores members that are themselves groups', () => {
    const nodes = [node('a', 0, 0), node('g0', 10, 10, { type: GROUP_TYPE }), node('b', 50, 50)];
    const res = groupNodes(nodes, ['a', 'g0', 'b'], 'g1');
    expect(childIdsOf(res!.nodes, 'g1').sort()).toEqual(['a', 'b']);
  });
});

describe('ungroupNodes', () => {
  it('removes the group and restores children to absolute positions', () => {
    const grouped = groupNodes([node('a', 100, 100), node('b', 300, 200)], ['a', 'b'], 'g')!.nodes;
    const out = ungroupNodes(grouped, 'g');
    expect(out.find((n) => n.id === 'g')).toBeUndefined();
    const a = out.find((n) => n.id === 'a')!;
    expect(a.parentId).toBeUndefined();
    expect(a.extent).toBeUndefined();
    // Restored to its original absolute position.
    expect(a.position).toEqual({ x: 100, y: 100 });
  });

  it('is a no-op for an unknown group id', () => {
    const nodes = [node('a', 0, 0)];
    expect(ungroupNodes(nodes, 'missing')).toBe(nodes);
  });
});

describe('toggleGroupCollapsed', () => {
  it('hides children and snaps to a compact chip, then restores exact size on expand', () => {
    const grouped = groupNodes([node('a', 100, 100), node('b', 300, 200)], ['a', 'b'], 'g')!.nodes;
    const g0 = grouped.find((n) => n.id === 'g')!;
    const expandedWidth = Number(g0.style!.width);
    const expandedHeight = Number(g0.style!.height);

    const collapsed = toggleGroupCollapsed(grouped, 'g');
    const g1 = collapsed.find((n) => n.id === 'g')!;
    expect(getGroupCollapsed(g1)).toBe(true);
    // Both dimensions shrink to the chip (not a full-width header bar).
    expect(g1.style!.width).toBe(GROUP_COLLAPSED_WIDTH);
    expect(g1.style!.height).toBe(GROUP_COLLAPSED_HEIGHT);
    expect(collapsed.find((n) => n.id === 'a')!.hidden).toBe(true);

    const expanded = toggleGroupCollapsed(collapsed, 'g');
    const g2 = expanded.find((n) => n.id === 'g')!;
    expect(getGroupCollapsed(g2)).toBe(false);
    // Restored verbatim from the remembered size, not recomputed.
    expect(g2.style!.width).toBe(expandedWidth);
    expect(g2.style!.height).toBe(expandedHeight);
    expect(expanded.find((n) => n.id === 'a')!.hidden).toBe(false);
  });

  it('remembers a size set while collapsed is irrelevant — expand uses the pre-collapse size', () => {
    const grouped = groupNodes([node('a', 0, 0), node('b', 200, 200)], ['a', 'b'], 'g')!.nodes;
    const w = Number(grouped.find((n) => n.id === 'g')!.style!.width);
    const collapsed = toggleGroupCollapsed(grouped, 'g');
    // Simulate the live (chip) width being whatever React Flow left in style.
    expect(toggleGroupCollapsed(collapsed, 'g').find((n) => n.id === 'g')!.style!.width).toBe(w);
  });
});

describe('offsetGroupChildren', () => {
  it('shifts only the group’s children and is a no-op for zero delta', () => {
    const grouped = groupNodes([node('a', 100, 100), node('b', 300, 200)], ['a', 'b'], 'g')!.nodes;
    const a0 = grouped.find((n) => n.id === 'a')!.position;

    const moved = offsetGroupChildren(grouped, 'g', 12, -8);
    const a1 = moved.find((n) => n.id === 'a')!.position;
    expect(a1).toEqual({ x: a0.x + 12, y: a0.y - 8 });
    // The group node itself (not a child of itself) is untouched.
    expect(moved.find((n) => n.id === 'g')!.position).toEqual(grouped.find((n) => n.id === 'g')!.position);

    expect(offsetGroupChildren(grouped, 'g', 0, 0)).toBe(grouped);
  });
});

describe('applyGroupCollapseOnLoad', () => {
  it('hides children of groups persisted as collapsed', () => {
    const nodes: RFNode[] = [
      { id: 'g', type: GROUP_TYPE, position: { x: 0, y: 0 }, data: { properties: { collapsed: true } } },
      node('a', 10, 10, { parentId: 'g' }),
      node('b', 20, 20), // not a child
    ];
    const out = applyGroupCollapseOnLoad(nodes);
    expect(out.find((n) => n.id === 'a')!.hidden).toBe(true);
    expect(out.find((n) => n.id === 'b')!.hidden).toBeFalsy();
  });

  it('returns the same array when no group is collapsed', () => {
    const nodes: RFNode[] = [{ id: 'g', type: GROUP_TYPE, position: { x: 0, y: 0 }, data: { properties: { collapsed: false } } }];
    expect(applyGroupCollapseOnLoad(nodes)).toBe(nodes);
  });
});

describe('applyGroupLabel', () => {
  it('renames only the target group', () => {
    const grouped = groupNodes([node('a', 0, 0), node('b', 50, 50)], ['a', 'b'], 'g')!.nodes;
    const out = applyGroupLabel(grouped, 'g', 'Ingestion');
    expect(getGroupLabel(out.find((n) => n.id === 'g')!)).toBe('Ingestion');
  });
});

describe('group colour', () => {
  it('defaults new groups to indigo and resolves known/unknown ids', () => {
    const grouped = groupNodes([node('a', 0, 0), node('b', 50, 50)], ['a', 'b'], 'g')!.nodes;
    expect(getGroupColorId(grouped.find((n) => n.id === 'g')!)).toBe(DEFAULT_GROUP_COLOR_ID);
    expect(getGroupColor('blue').id).toBe('blue');
    expect(getGroupColor('nope')).toEqual(GROUP_COLORS[0]);
    // Every derived value is translucent (no opaque body fill).
    expect(getGroupColor('blue').body).toContain('rgba(');
  });

  it('recolours only the target group, preserving label/collapsed', () => {
    const grouped = groupNodes([node('a', 0, 0), node('b', 50, 50)], ['a', 'b'], 'g')!.nodes;
    const out = applyGroupColor(applyGroupLabel(grouped, 'g', 'Ingestion'), 'g', 'amber');
    const g = out.find((n) => n.id === 'g')!;
    expect(getGroupColorId(g)).toBe('amber');
    expect(getGroupLabel(g)).toBe('Ingestion');
    expect(getGroupCollapsed(g)).toBe(false);
  });
});

describe('directChildCount', () => {
  it('counts only direct children of the group', () => {
    const grouped = groupNodes([node('a', 0, 0), node('b', 50, 50)], ['a', 'b'], 'g')!.nodes;
    const withOutsider = [...grouped, node('c', 999, 999)];
    expect(directChildCount(withOutsider, 'g')).toBe(2);
    expect(directChildCount(withOutsider, 'missing')).toBe(0);
  });
});
