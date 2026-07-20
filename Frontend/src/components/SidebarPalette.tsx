// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { memo, useDeferredValue, useEffect, useState } from 'react';
import type { DragEvent } from 'react';
import { ChevronDown, ChevronRight, GripVertical, LayoutTemplate, Pin, Search, Upload } from 'lucide-react';
import type { GalleryTemplate, NodePackageSummary } from '../types';
import { api } from '../utils/api';
import { useCanvasStore } from '../stores/useCanvasStore';
import { NodeIcon } from './nodeIcons';

/** A template entry in the palette, tagged with which catalog it came from. */
interface PaletteTemplate {
  source: 'gallery' | 'library';
  template: GalleryTemplate;
}

/** A template can't be dropped-and-inserted directly if it has a required parameter with no default. */
function templateNeedsConfig(template: GalleryTemplate): boolean {
  return template.manifest.parameters.some((p) => p.required && (p.default == null || p.default === ''));
}

interface SidebarPaletteProps {
  availableNodes: NodePackageSummary[];
  onAddNode: (nodePackage: NodePackageSummary) => void;
  onDragStart: (event: DragEvent<HTMLButtonElement>, nodePackage: NodePackageSummary) => void;
  onImportOpenApi?: () => void;
}

interface PaletteNodeItem {
  nodePackage: NodePackageSummary;
  displayName: string;
  description: string;
  category: PaletteCategory;
  /** Manifest opt-in: an escape-hatch node de-emphasized into the collapsed "Advanced" section. */
  secondary: boolean;
  searchText: string;
}

type PaletteCategory = 'Trigger' | 'Logic' | 'Data' | 'Network' | 'Ai' | 'Utility';
type PaletteSection = 'RecentPinned' | PaletteCategory | 'Advanced';
/** Every reorderable palette panel, including Templates (which has its own renderer). */
type PaletteSectionKey = PaletteSection | 'Templates';

const orderedCategories: PaletteCategory[] = ['Trigger', 'Logic', 'Data', 'Network', 'Ai', 'Utility'];

// Default panel order, tuned for usage likelihood: your own nodes first, then the core building
// blocks (trigger → logic → data → network → ai → utility), with the browse-y Templates panel and
// the collapsed Advanced escape-hatches last. Users can drag panels to reorder (persisted below).
const DEFAULT_SECTION_ORDER: PaletteSectionKey[] = ['RecentPinned', 'Trigger', 'Logic', 'Data', 'Network', 'Ai', 'Utility', 'Templates', 'Advanced'];
const PALETTE_SECTION_ORDER_KEY = 'knotarium-palette-section-order';

/** Load the persisted panel order, reconciled against the current known panels (forward/back compatible). */
function loadPaletteSectionOrder(): PaletteSectionKey[] {
  const known = new Set<string>(DEFAULT_SECTION_ORDER);
  try {
    const raw = localStorage.getItem(PALETTE_SECTION_ORDER_KEY);
    if (!raw) return [...DEFAULT_SECTION_ORDER];
    const saved = JSON.parse(raw) as unknown;
    if (!Array.isArray(saved)) return [...DEFAULT_SECTION_ORDER];
    // Keep the saved order (valid keys only), then append any panels added since (so new panels show up).
    const ordered = saved.filter((k): k is PaletteSectionKey => typeof k === 'string' && known.has(k));
    for (const k of DEFAULT_SECTION_ORDER) if (!ordered.includes(k)) ordered.push(k);
    return ordered;
  } catch {
    return [...DEFAULT_SECTION_ORDER];
  }
}

// Editor-only annotation node types are inserted from the canvas toolbar (a note button,
// "group selection"), never dragged from the palette — so hide them here even though the
// backend registers them as known node packages.
const ANNOTATION_NODE_IDS = new Set(['stickyNote', 'group']);

// Built-in node types no longer offered in the palette. `manualTrigger` is redundant with `start`
// (both are manual-run entry points — see TriggerEntryResolver), so it's hidden from new workflows.
// It stays registered/executable so any existing workflow that already contains one still renders and
// runs; only the palette stops offering it.
const PALETTE_HIDDEN_NODE_IDS = new Set(['manualTrigger']);

function getLatestManifest(nodePackage: NodePackageSummary): Record<string, unknown> | null {
  const latestVersion = [...(nodePackage.versions || [])].sort((left, right) => {
    return new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime();
  })[0];

  if (!latestVersion?.manifestJson) {
    return null;
  }

  try {
    return JSON.parse(latestVersion.manifestJson) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function normalizeCategory(category: string | null | undefined): PaletteCategory {
  switch ((category || '').trim().toLowerCase()) {
    case 'trigger':
    case 'triggers':
      return 'Trigger';
    case 'control':
    case 'logic':
      return 'Logic';
    case 'data':
      return 'Data';
    case 'integrations':
    case 'integration':
    case 'network':
      return 'Network';
    case 'ai':
      return 'Ai';
    case 'utility':
    default:
      return 'Utility';
  }
}

function buildPaletteItem(nodePackage: NodePackageSummary): PaletteNodeItem {
  const manifest = getLatestManifest(nodePackage);
  const displayName = typeof manifest?.displayName === 'string' ? manifest.displayName : nodePackage.displayName;
  const description = typeof manifest?.description === 'string' ? manifest.description : '';
  const category = normalizeCategory(typeof manifest?.category === 'string' ? manifest.category : nodePackage.category);
  const secondary = manifest?.secondary === true;
  // Match only what the user can SEE (name, description, category) — not the internal node id. Matching
  // the id surprised people (e.g. searching "x" hit the external-device package via its id "e[x]ternalDevice").
  const searchText = [displayName, description, category].join(' ').toLowerCase();

  return {
    nodePackage,
    displayName,
    description,
    category,
    secondary,
    searchText,
  };
}

function SidebarPaletteImpl({ availableNodes, onAddNode, onDragStart, onImportOpenApi }: SidebarPaletteProps) {
  const [searchQuery, setSearchQuery] = useState('');
  const [collapsedCategories, setCollapsedCategories] = useState<Partial<Record<PaletteSection, boolean>>>({});
  const [templates, setTemplates] = useState<PaletteTemplate[]>([]);
  const [templatesCollapsed, setTemplatesCollapsed] = useState(false);
  const pinnedNodeIds = useCanvasStore((state) => state.pinnedNodeIds);
  const recentNodeIds = useCanvasStore((state) => state.recentNodeIds);
  const togglePinNode = useCanvasStore((state) => state.togglePinNode);
  const deferredSearchQuery = useDeferredValue(searchQuery);
  const queryTokens = deferredSearchQuery.trim().toLowerCase().split(/\s+/).filter(Boolean);
  // While searching, empty groups are noise — hide them (and show one global "no matches" line instead).
  const isSearching = queryTokens.length > 0;

  // ── Reorderable panels ── The panel order is user-adjustable (drag a panel's grip) and persisted.
  const [sectionOrder, setSectionOrder] = useState<PaletteSectionKey[]>(loadPaletteSectionOrder);
  const [draggingSection, setDraggingSection] = useState<PaletteSectionKey | null>(null);
  const [dragOverSection, setDragOverSection] = useState<PaletteSectionKey | null>(null);
  useEffect(() => {
    try { localStorage.setItem(PALETTE_SECTION_ORDER_KEY, JSON.stringify(sectionOrder)); } catch { /* non-fatal */ }
  }, [sectionOrder]);

  // Move `from` to sit just before `target` in the order.
  const moveSection = (from: PaletteSectionKey, target: PaletteSectionKey) => {
    if (from === target) return;
    setSectionOrder((current) => {
      const next = current.filter((key) => key !== from);
      const targetIndex = next.indexOf(target);
      if (targetIndex < 0) return current;
      next.splice(targetIndex, 0, from);
      return next;
    });
  };

  // Drag props for a panel's grip handle (the drag source).
  const gripProps = (key: PaletteSectionKey) => ({
    draggable: true,
    onDragStart: (event: DragEvent<HTMLElement>) => {
      setDraggingSection(key);
      event.dataTransfer.setData('application/knotarium-palette-section', key);
      event.dataTransfer.effectAllowed = 'move';
    },
    onDragEnd: () => { setDraggingSection(null); setDragOverSection(null); },
  });

  // Drag props for a panel body (the drop target) — only reacts to a panel drag, never a node/template drag.
  const sectionDropProps = (key: PaletteSectionKey) => ({
    onDragOver: (event: DragEvent<HTMLElement>) => {
      if (draggingSection && draggingSection !== key) { event.preventDefault(); setDragOverSection(key); }
    },
    onDrop: (event: DragEvent<HTMLElement>) => {
      if (!draggingSection) return;
      event.preventDefault();
      moveSection(draggingSection, key);
      setDraggingSection(null);
      setDragOverSection(null);
    },
    onDragLeave: () => setDragOverSection((current) => (current === key ? null : current)),
  });

  // A drag-handle grip rendered at the left of each panel header.
  const renderGrip = (key: PaletteSectionKey) => (
    <span
      {...gripProps(key)}
      onClick={(event) => event.stopPropagation()}
      role="button"
      tabIndex={-1}
      aria-label="Drag to reorder panel"
      title="Drag to reorder"
      style={{ display: 'inline-flex', alignItems: 'center', cursor: 'grab', color: 'rgba(148,163,184,0.7)', marginRight: '-2px' }}
    >
      <GripVertical size={14} />
    </span>
  );

  useEffect(() => {
    let cancelled = false;
    Promise.all([api.listGalleryTemplates(), api.listLibraryTemplates()])
      .then(([gallery, library]) => {
        if (cancelled) return;
        setTemplates([
          ...library.map((template) => ({ source: 'library' as const, template })),
          ...gallery.map((template) => ({ source: 'gallery' as const, template })),
        ]);
      })
      .catch(() => { /* the palette still works without templates */ });
    return () => { cancelled = true; };
  }, []);

  const filteredTemplates = templates.filter(({ template }) => {
    const m = template.manifest;
    // Match the visible card text (name + description), not the hidden tags — searching "x" matched the
    // unshown "example" tag of "Hello World", which reads as a random hit. Mirrors the node-search change.
    const haystack = `${m.name} ${m.description}`.toLowerCase();
    return queryTokens.every((token) => haystack.includes(token));
  });

  const filteredItems = availableNodes
    .filter(nodePackage => !ANNOTATION_NODE_IDS.has(nodePackage.id) && !PALETTE_HIDDEN_NODE_IDS.has(nodePackage.id))
    .map(buildPaletteItem)
    .filter(item => queryTokens.every(token => item.searchText.includes(token)))
    .sort((left, right) => left.displayName.localeCompare(right.displayName));

  // Escape-hatch nodes (manifest secondary=true) drop out of the main categories into a single
  // collapsed "Advanced" section, so the primary block is the one obvious choice.
  const primaryItems = filteredItems.filter(item => !item.secondary);
  const advancedItems = filteredItems.filter(item => item.secondary);

  const groupedItems = orderedCategories.reduce<Record<PaletteCategory, PaletteNodeItem[]>>((accumulator, category) => {
    accumulator[category] = primaryItems.filter(item => item.category === category);
    return accumulator;
  }, {
    Trigger: [],
    Logic: [],
    Data: [],
    Network: [],
    Ai: [],
    Utility: [],
  });

  const filteredItemsById = new Map(filteredItems.map((item) => [item.nodePackage.id, item]));
  const recentAndPinnedItems = [...pinnedNodeIds, ...recentNodeIds.filter((nodeId) => !pinnedNodeIds.includes(nodeId))]
    .map((nodeId) => filteredItemsById.get(nodeId))
    .filter((item): item is PaletteNodeItem => Boolean(item));

  const renderPaletteItem = (item: PaletteNodeItem) => {
    const isPinned = pinnedNodeIds.includes(item.nodePackage.id);
    const isRecent = recentNodeIds.includes(item.nodePackage.id);
    const badges = [isPinned ? 'Pinned' : null, isRecent ? 'Recent' : null].filter(Boolean).join(' · ');

    return (
      <div
        key={item.nodePackage.id}
        style={{
          display: 'grid',
          gridTemplateColumns: '1fr auto',
          gap: '8px',
          alignItems: 'stretch',
        }}
      >
        <button
          type="button"
          draggable
          data-testid={`palette-node-${item.nodePackage.id}`}
          onDragStart={(event) => onDragStart(event, item.nodePackage)}
          onClick={() => onAddNode(item.nodePackage)}
          title={item.description || item.displayName}
          style={{
            display: 'flex',
            alignItems: 'flex-start',
            gap: '10px',
            width: '100%',
            padding: '12px',
            borderRadius: '12px',
            border: '1px solid rgba(255,255,255,0.07)',
            background: 'linear-gradient(180deg, rgba(17, 24, 39, 0.92) 0%, rgba(15, 23, 42, 0.84) 100%)',
            color: '#f8fafc',
            cursor: 'grab',
            textAlign: 'left',
          }}
        >
          <NodeIcon nodeId={item.nodePackage.id} category={item.category} size={34} glyphSize={18} />
          <span style={{ display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0 }}>
            <span style={{ fontSize: '0.88rem', fontWeight: 700 }}>{item.displayName}</span>
            <span style={{ fontSize: '0.76rem', color: 'rgba(148,163,184,0.95)' }}>
              {item.description || `${item.category} node`}
            </span>
            {badges ? (
              <span style={{ fontSize: '0.7rem', color: 'rgba(125,211,252,0.95)', fontWeight: 700 }}>
                {badges}
              </span>
            ) : null}
          </span>
        </button>

        <button
          type="button"
          aria-label={isPinned ? `Unpin ${item.displayName}` : `Pin ${item.displayName}`}
          data-testid={`palette-pin-${item.nodePackage.id}`}
          onClick={() => togglePinNode(item.nodePackage.id)}
          style={{
            width: '42px',
            minWidth: '42px',
            borderRadius: '12px',
            border: '1px solid rgba(255,255,255,0.07)',
            background: isPinned ? 'rgba(56, 189, 248, 0.16)' : 'rgba(15, 23, 42, 0.75)',
            color: isPinned ? 'rgb(125, 211, 252)' : 'rgba(148,163,184,0.95)',
            cursor: 'pointer',
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
        >
          <Pin size={15} />
        </button>
      </div>
    );
  };

  const renderSection = (section: PaletteSection, label: string, items: PaletteNodeItem[]) => {
    // While searching, drop sections with no hits rather than stacking "No matching nodes." headers.
    if (isSearching && items.length === 0) return null;
    // "Advanced" starts collapsed (escape hatches stay out of the way until sought).
    const isCollapsed = collapsedCategories[section] ?? section === 'Advanced';

    const headerBg = section === 'RecentPinned' ? 'rgba(56, 189, 248, 0.08)' : 'rgba(255,255,255,0.03)';
    return (
      <section
        key={section}
        {...sectionDropProps(section)}
        data-testid={`palette-category-${section === 'RecentPinned' ? 'recent-pinned' : section.toLowerCase()}`}
        style={{
          marginBottom: '10px',
          borderRadius: '14px',
          border: '1px solid rgba(255,255,255,0.07)',
          background: section === 'RecentPinned' ? 'rgba(56, 189, 248, 0.06)' : 'rgba(255,255,255,0.025)',
          overflow: 'hidden',
          opacity: draggingSection === section ? 0.5 : 1,
          boxShadow: dragOverSection === section ? 'inset 0 3px 0 0 var(--color-accent, #6f6cf0)' : undefined,
        }}
      >
        <div style={{ display: 'flex', alignItems: 'stretch', background: headerBg }}>
          <span style={{ display: 'flex', alignItems: 'center', paddingLeft: '10px' }}>{renderGrip(section)}</span>
          <button
            type="button"
            onClick={() => setCollapsedCategories(current => ({ ...current, [section]: !(current[section] ?? section === 'Advanced') }))}
            aria-expanded={!isCollapsed}
            style={{
              flex: 1,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              padding: '12px 14px 12px 8px',
              border: 'none',
              background: 'transparent',
              color: '#e2e8f0',
              cursor: 'pointer',
              fontWeight: 700,
              fontSize: '0.8rem',
              letterSpacing: '0.04em',
              textTransform: 'uppercase',
            }}
          >
            <span style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              {isCollapsed ? <ChevronRight size={16} /> : <ChevronDown size={16} />}
              {label}
            </span>
            <span style={{ color: 'rgba(148,163,184,0.9)' }}>{items.length}</span>
          </button>
        </div>

        {!isCollapsed && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', padding: '10px' }}>
            {items.length === 0 ? (
              <div style={{ padding: '10px 12px', color: 'rgba(148,163,184,0.9)', fontSize: '0.82rem' }}>
                No matching nodes.
              </div>
            ) : (
              items.map(renderPaletteItem)
            )}
          </div>
        )}
      </section>
    );
  };

  const renderTemplateItem = ({ source, template }: PaletteTemplate) => {
    const m = template.manifest;
    const needsConfig = templateNeedsConfig(template);
    return (
      <button
        key={`${source}:${template.templateId}`}
        type="button"
        draggable
        data-testid={`palette-template-${template.templateId}`}
        onDragStart={(event) => {
          event.dataTransfer.setData(
            'application/knotarium-template',
            JSON.stringify({ source, templateId: template.templateId, needsConfig }),
          );
          // Must match the canvas drop target's dropEffect ('move'); 'copy' here makes the browser reject the drop.
          event.dataTransfer.effectAllowed = 'move';
        }}
        title={`Drag onto the canvas to insert “${m.name}”${needsConfig ? ' (asks for values first)' : ''}`}
        style={{
          display: 'flex', alignItems: 'flex-start', gap: '10px', width: '100%', padding: '12px',
          borderRadius: '12px', border: '1px solid rgba(255,255,255,0.07)',
          background: 'linear-gradient(180deg, rgba(17, 24, 39, 0.92) 0%, rgba(15, 23, 42, 0.84) 100%)',
          color: '#f8fafc', cursor: 'grab', textAlign: 'left',
        }}
      >
        <span
          aria-hidden="true"
          style={{
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center', width: '30px', height: '30px',
            borderRadius: '10px', background: 'rgba(139, 124, 240, 0.14)', color: '#9d9af8', flexShrink: 0,
          }}
        >
          <LayoutTemplate size={15} />
        </span>
        <span style={{ display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0 }}>
          <span style={{ fontSize: '0.88rem', fontWeight: 700 }}>{m.name}</span>
          <span style={{ fontSize: '0.76rem', color: 'rgba(148,163,184,0.95)' }}>
            {m.description || 'Template'}
          </span>
          <span style={{ fontSize: '0.7rem', color: 'rgba(157,154,248,0.95)', fontWeight: 700 }}>
            {source === 'library' ? 'Your library' : 'Gallery'} · drops a subgraph
          </span>
        </span>
      </button>
    );
  };

  const renderTemplatesSection = () => {
    if (isSearching && filteredTemplates.length === 0) return null;
    return (
    <section
      key="Templates"
      {...sectionDropProps('Templates')}
      data-testid="palette-category-templates"
      style={{
        marginBottom: '10px', borderRadius: '14px', border: '1px solid rgba(139,124,240,0.18)',
        background: 'rgba(139, 124, 240, 0.06)', overflow: 'hidden',
        opacity: draggingSection === 'Templates' ? 0.5 : 1,
        boxShadow: dragOverSection === 'Templates' ? 'inset 0 3px 0 0 var(--color-accent, #6f6cf0)' : undefined,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'stretch', background: 'rgba(139, 124, 240, 0.08)' }}>
        <span style={{ display: 'flex', alignItems: 'center', paddingLeft: '10px' }}>{renderGrip('Templates')}</span>
        <button
          type="button"
          onClick={() => setTemplatesCollapsed((c) => !c)}
          aria-expanded={!templatesCollapsed}
          style={{
            flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'space-between',
            padding: '12px 14px 12px 8px', border: 'none', background: 'transparent', color: '#e2e8f0',
            cursor: 'pointer', fontWeight: 700, fontSize: '0.8rem', letterSpacing: '0.04em', textTransform: 'uppercase',
          }}
        >
          <span style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            {templatesCollapsed ? <ChevronRight size={16} /> : <ChevronDown size={16} />}
            Templates
          </span>
          <span style={{ color: 'rgba(148,163,184,0.9)' }}>{filteredTemplates.length}</span>
        </button>
      </div>

      {!templatesCollapsed && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', padding: '10px' }}>
          {filteredTemplates.length === 0 ? (
            <div style={{ padding: '10px 12px', color: 'rgba(148,163,184,0.9)', fontSize: '0.82rem' }}>
              {templates.length === 0 ? 'No templates yet — save one from a workflow or the gallery.' : 'No matching templates.'}
            </div>
          ) : (
            filteredTemplates.map(renderTemplateItem)
          )}
        </div>
      )}
    </section>
    );
  };

  return (
    <aside
      style={{
        width: '320px',
        minWidth: '320px',
        display: 'flex',
        flexDirection: 'column',
        background: 'linear-gradient(180deg, rgba(7, 11, 20, 0.96) 0%, rgba(12, 19, 33, 0.92) 100%)',
        borderRight: '1px solid var(--border-color)',
        backdropFilter: 'blur(18px)',
        boxShadow: 'inset -1px 0 0 rgba(255,255,255,0.03)',
      }}
    >
      <div
        style={{
          position: 'sticky',
          top: 0,
          zIndex: 2,
          padding: '20px 18px 14px',
          borderBottom: '1px solid rgba(255,255,255,0.08)',
          background: 'linear-gradient(180deg, rgba(10, 15, 26, 0.98) 0%, rgba(10, 15, 26, 0.88) 100%)',
        }}
      >
        <div style={{ color: '#f8fafc', fontSize: '1rem', fontWeight: 700, marginBottom: '12px' }}>
          Node Palette
        </div>
        <label
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '10px',
            padding: '11px 12px',
            borderRadius: '12px',
            background: 'rgba(255,255,255,0.04)',
            border: '1px solid rgba(255,255,255,0.08)',
          }}
        >
          <Search size={15} color="rgba(226,232,240,0.82)" />
          <input
            type="search"
            value={searchQuery}
            onChange={(event) => setSearchQuery(event.target.value)}
            placeholder="Search nodes"
            aria-label="Search nodes"
            style={{
              width: '100%',
              background: 'transparent',
              border: 'none',
              outline: 'none',
              color: '#fff',
              fontSize: '0.9rem',
            }}
          />
        </label>

        {onImportOpenApi && (
          <button
            type="button"
            onClick={onImportOpenApi}
            style={{
              marginTop: '10px',
              width: '100%',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              gap: '7px',
              padding: '9px 14px',
              borderRadius: '10px',
              background: 'rgba(111,108,240,.12)',
              border: '1px solid rgba(111,108,240,.3)',
              color: '#9d9af8',
              fontSize: '0.82rem',
              fontWeight: 600,
              cursor: 'pointer',
            }}
          >
            <Upload size={13} />
            Import OpenAPI…
          </button>
        )}
      </div>

      <div style={{ flex: 1, overflowY: 'auto', padding: '12px 12px 20px' }}>
        {sectionOrder.map((key) => {
          if (key === 'Templates') return renderTemplatesSection();
          if (key === 'RecentPinned') return renderSection('RecentPinned', 'Recent / Pinned', recentAndPinnedItems);
          if (key === 'Advanced') return advancedItems.length > 0 ? renderSection('Advanced', 'Advanced', advancedItems) : null;
          return renderSection(key, key, groupedItems[key]);
        })}
        {isSearching && filteredItems.length === 0 && filteredTemplates.length === 0 && (
          <div style={{ padding: '16px 12px', color: 'rgba(148,163,184,0.9)', fontSize: '0.85rem', textAlign: 'center' }}>
            No nodes or templates match “{deferredSearchQuery.trim()}”.
          </div>
        )}
      </div>
    </aside>
  );
}

// Memoized: SidebarPalette is a Canvas child that rebuilds the whole node catalog (map + several filters)
// on each render, yet Canvas re-renders on every hover/drag frame (it subscribes to hoveredNodeId etc.).
// Its props are stable (availableNodes + useCallback handlers), and it subscribes only to useCanvasStore
// (pins/recent) — none of which change on hover — so memo keeps it from re-rendering during interactions.
export const SidebarPalette = memo(SidebarPaletteImpl);