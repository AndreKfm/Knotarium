// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import {
  Activity, BookOpen, Code, Compass, Edit3, FileInput, Globe, Grid, Inbox,
  LayoutTemplate, Package, Search, Settings, ShieldCheck, ShieldOff, Sparkles, Users,
} from 'lucide-react';
import { RuntimeArmingToggle } from '../RuntimeArmingToggle';
import { UserMenu } from '../auth/UserMenu';
import { useAuth } from '../auth/AuthContext';
import { CommandPalette, type PaletteEntry } from './CommandPalette';
import { useAdaptiveBar } from './useAdaptiveBar';
import type { VersionInfo } from '../../types';
import './topbar.css';

/**
 * The application top bar.
 *
 * Every destination stays in the bar at every width — what degrades under
 * pressure is labels, in the fixed order encoded by `shed` below, measured by
 * useAdaptiveBar(). The active destination is excluded from that order: "where
 * am I" is never a guessing game, so it keeps its label and its accent inset
 * even in the all-icon row.
 *
 * Actions that are not destinations (tour, sign out) live in the account menu,
 * and everything the bar can reach is also reachable by name from ⌘K — which
 * is what makes an icon-only row honest rather than a memory test.
 */

export type TopBarTarget =
  | 'dashboard' | 'editor' | 'node-editor'
  | 'execution' | 'dead-letter'
  | 'bundles' | 'templates' | 'api-importer' | 'imports'
  | 'settings' | 'users';

interface TopBarProps {
  /** App's current view (an unknown value simply means "nothing here is active"). */
  view: string;
  onSelect: (target: TopBarTarget) => void;
  /** Open the AI dialog either empty or seeded with the open canvas. */
  onAiGenerate: (mode: 'new' | 'extend') => void;
  onOpenWorkflow: (workflowId: string) => void;
  onOpenRun: (executionId: string) => void;
  /** Resolve and open the most recent run (used by ⌘K and by the Run group). */
  onOpenLatestRun: () => void;
  onOpenTour: () => void;
  armed: boolean | null;
  armingBusy: boolean;
  onSetArmed: (armed: boolean) => void;
  version: VersionInfo | null;
  onGoHome: () => void;
}

interface Destination {
  id: TopBarTarget | 'ai-generate';
  label: string;
  icon: ReactNode;
  /** A · Build, B · Run, C · Library, D · Admin — separators carry the grouping
   *  once labels are gone; twelve identical-weight icons in a row do not read. */
  group: 'A' | 'B' | 'C' | 'D';
  /** Shed priority — lower sheds its label first. */
  shed: number;
  active: boolean;
  run: () => void;
  synonyms: string[];
  tour?: string;
}

/** Build stamp in a FIXED format (local time, 24h) so it reads identically on any machine. */
function formatBuildTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** Ticking wall clock, isolated so the per-second update does not re-render the bar. */
function Clock() {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = window.setInterval(() => setNow(new Date()), 1000);
    return () => window.clearInterval(id);
  }, []);
  return <>{now.toLocaleTimeString()}</>;
}

export function TopBar({
  view, onSelect, onAiGenerate, onOpenWorkflow, onOpenRun, onOpenLatestRun,
  onOpenTour, armed, armingBusy, onSetArmed, version, onGoHome,
}: TopBarProps) {
  const { status } = useAuth();
  const authenticated = status?.authenticated === true;
  const { barRef, spacerRef, navRef, relayout } = useAdaptiveBar();
  const [paletteOpen, setPaletteOpen] = useState(false);

  const destinations = useMemo<Destination[]>(() => {
    const all: Destination[] = [
      // A · Build
      { id: 'dashboard', label: 'Dashboard', icon: <Grid size={16} />, group: 'A', shed: 12, active: view === 'dashboard', run: () => onSelect('dashboard'), synonyms: ['home', 'overview', 'start', 'workflows'], tour: 'dashboard' },
      { id: 'editor', label: 'Canvas Editor', icon: <Edit3 size={16} />, group: 'A', shed: 10, active: view === 'editor', run: () => onSelect('editor'), synonyms: ['canvas', 'build', 'design', 'new workflow', 'graph'], tour: 'canvas-editor' },
      { id: 'ai-generate', label: 'AI Generate', icon: <Sparkles size={16} />, group: 'A', shed: 9, active: false, run: () => onAiGenerate(view === 'editor' ? 'extend' : 'new'), synonyms: ['ai', 'generate', 'prompt', 'assistant'], tour: 'ai-generate' },
      { id: 'node-editor', label: 'Node Editor', icon: <Code size={16} />, group: 'A', shed: 8, active: view === 'node-editor', run: () => onSelect('node-editor'), synonyms: ['custom node', 'code', 'author'] },
      // B · Run
      { id: 'execution', label: 'Execution Visualizer', icon: <Activity size={16} />, group: 'B', shed: 11, active: view === 'execution', run: () => onSelect('execution'), synonyms: ['logs', 'runs', 'execution', 'trace', 'live', 'monitor'] },
      { id: 'dead-letter', label: 'Dead Letter', icon: <Inbox size={16} />, group: 'B', shed: 7, active: view === 'dead-letter', run: () => onSelect('dead-letter'), synonyms: ['failed', 'errors', 'dlq', 'retry'], tour: 'dead-letter' },
      // C · Library
      { id: 'bundles', label: 'Bundles', icon: <Package size={16} />, group: 'C', shed: 6, active: view === 'bundles', run: () => onSelect('bundles'), synonyms: ['package', 'export', 'install', 'share'] },
      { id: 'templates', label: 'Templates', icon: <LayoutTemplate size={16} />, group: 'C', shed: 5, active: view === 'templates', run: () => onSelect('templates'), synonyms: ['gallery', 'starter', 'sample', 'example'], tour: 'templates' },
      { id: 'api-importer', label: 'API Importer', icon: <Globe size={16} />, group: 'C', shed: 4, active: view === 'api-importer', run: () => onSelect('api-importer'), synonyms: ['openapi', 'swagger', 'rest', 'http'] },
      { id: 'imports', label: 'Import', icon: <FileInput size={16} />, group: 'C', shed: 3, active: view === 'imports', run: () => onSelect('imports'), synonyms: ['settings import', 'migrate', 'restore'] },
      // D · Admin
      { id: 'settings', label: 'Settings', icon: <Settings size={16} />, group: 'D', shed: 2, active: view === 'settings', run: () => onSelect('settings'), synonyms: ['preferences', 'config', 'safety', 'permissions', 'backup'], tour: 'settings' },
    ];
    // User management only exists when authentication is switched on.
    if (authenticated) {
      all.push({ id: 'users', label: 'Users', icon: <Users size={16} />, group: 'D', shed: 1, active: false, run: () => onSelect('users'), synonyms: ['accounts', 'people', 'admin', 'roles'] });
    }
    return all;
  }, [view, authenticated, onSelect, onAiGenerate]);

  // Shed ranks: 1..N over everything EXCEPT the active destination, which never
  // reaches the shed list. Recomputed whenever the active item changes.
  const shedRanks = useMemo(() => {
    const ranks = new Map<string, number>();
    destinations
      .filter((d) => !d.active)
      .sort((a, b) => a.shed - b.shed)
      .forEach((d, index) => ranks.set(d.id, index + 1));
    return ranks;
  }, [destinations]);

  // The rank attributes change with the active item, so re-measure after they
  // land. Keyed on the actual composition rather than on object identity, so a
  // parent re-render with fresh callbacks does not force a layout pass.
  const layoutKey = destinations.map((d) => `${d.id}${d.active ? '*' : ''}`).join('|');
  useEffect(() => { relayout(); }, [relayout, layoutKey]);

  // ── tooltip (portaled: .tb clips its own children) ──────────────────────
  const [tip, setTip] = useState<{ text: string; x: number; y: number } | null>(null);
  const tipTimer = useRef<number | null>(null);
  const clearTip = useCallback(() => {
    if (tipTimer.current !== null) window.clearTimeout(tipTimer.current);
    tipTimer.current = null;
  }, []);
  const hideTip = useCallback(() => { clearTip(); setTip(null); }, [clearTip]);
  const armTip = useCallback((element: HTMLElement, text: string, always = false) => {
    clearTip();
    // 400ms: shorter fires on every pass-through, longer reads as broken.
    tipTimer.current = window.setTimeout(() => {
      // Re-check at fire time — a label that is still visible needs no tooltip.
      if (!always && !element.classList.contains('iconly')) return;
      const rect = element.getBoundingClientRect();
      setTip({
        text,
        x: Math.min(Math.max(rect.left + rect.width / 2, 70), window.innerWidth - 70),
        // Hung off the bar's bottom edge, not the item's, so tooltips line up
        // with each other instead of overlapping the bar they came from.
        y: (barRef.current?.getBoundingClientRect().bottom ?? rect.bottom) + 8,
      });
    }, 400);
  }, [clearTip, barRef]);
  useEffect(() => hideTip, [hideTip]);

  const tipProps = (text: string, always = false) => ({
    onPointerEnter: (e: React.PointerEvent<HTMLElement>) => armTip(e.currentTarget, text, always),
    onPointerLeave: hideTip,
    onFocus: (e: React.FocusEvent<HTMLElement>) => armTip(e.currentTarget, text, always),
    onBlur: hideTip,
    // Not onClick — these props are spread over elements that carry their own
    // click handler, and a second onClick would silently replace it.
    onPointerDown: hideTip,
  });

  // ── ⌘K ──────────────────────────────────────────────────────────────────
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && (event.key === 'k' || event.key === 'K')) {
        event.preventDefault();
        setPaletteOpen((open) => !open);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const paletteEntries = useMemo<PaletteEntry[]>(() => {
    const destinationEntries: PaletteEntry[] = destinations.map((d) => ({
      id: `nav:${d.id}`,
      group: 'Destinations',
      label: d.label,
      icon: d.icon,
      synonyms: d.synonyms,
      hint: d.active ? 'current' : undefined,
      run: d.run,
    }));
    const actions: PaletteEntry[] = [
      { id: 'act:arm', group: 'Actions', label: 'Arm system', icon: <ShieldCheck size={16} />, synonyms: ['enable', 'schedule', 'live', 'runtime'], run: () => onSetArmed(true) },
      { id: 'act:disarm', group: 'Actions', label: 'Disarm system', icon: <ShieldOff size={16} />, synonyms: ['pause', 'safe', 'stop', 'runtime'], run: () => onSetArmed(false) },
      { id: 'act:replay', group: 'Actions', label: 'Replay last run…', icon: <Activity size={16} />, hint: 'opens the newest run', synonyms: ['rerun', 'again', 'step through', 'latest'], run: onOpenLatestRun },
      { id: 'act:ai-new', group: 'Actions', label: 'AI Generate — new workflow', icon: <Sparkles size={16} />, synonyms: ['ai', 'draft', 'prompt'], run: () => onAiGenerate('new') },
      { id: 'act:import-bundle', group: 'Actions', label: 'Import bundle', icon: <Package size={16} />, synonyms: ['install', 'package', 'restore'], run: () => onSelect('bundles') },
      { id: 'act:tour', group: 'Actions', label: 'Product tour', icon: <Compass size={16} />, synonyms: ['help', 'guide', 'onboarding'], run: onOpenTour },
    ];
    if (view === 'editor') {
      actions.splice(4, 0, {
        id: 'act:ai-extend', group: 'Actions', label: 'AI Generate — extend canvas',
        icon: <Sparkles size={16} />, synonyms: ['ai', 'refine', 'add to canvas'],
        run: () => onAiGenerate('extend'),
      });
    }
    return [...destinationEntries, ...actions];
  }, [destinations, view, onSetArmed, onOpenLatestRun, onAiGenerate, onSelect, onOpenTour]);

  // ── roving arrow keys across the nav row ────────────────────────────────
  const onNavKeyDown = (event: React.KeyboardEvent<HTMLElement>) => {
    const keys = ['ArrowLeft', 'ArrowRight', 'Home', 'End'];
    if (!keys.includes(event.key)) return;
    const items = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('.ni'));
    const current = items.indexOf(document.activeElement as HTMLElement);
    if (current < 0) return;
    event.preventDefault();
    const next =
      event.key === 'Home' ? 0
        : event.key === 'End' ? items.length - 1
          : event.key === 'ArrowLeft' ? (current - 1 + items.length) % items.length
            : (current + 1) % items.length;
    items[next]?.focus();
  };

  const rows: ReactNode[] = [];
  destinations.forEach((destination, index) => {
    if (index > 0 && destinations[index - 1].group !== destination.group) {
      rows.push(<span key={`sep-${destination.group}`} className="tb-sep" aria-hidden="true" />);
    }
    rows.push(
      <button
        key={destination.id}
        type="button"
        data-tour={destination.tour}
        data-shed-rank={shedRanks.get(destination.id) ?? 0}
        className={`ni${destination.active ? ' on' : ''}`}
        aria-label={destination.label}
        aria-current={destination.active ? 'page' : undefined}
        onClick={destination.run}
        {...tipProps(destination.label)}
      >
        <span className="ni-icon" aria-hidden="true">{destination.icon}</span>
        <span className="ni-label">{destination.label}</span>
      </button>,
    );
  });

  return (
    <header className="tb" ref={barRef}>
      <button type="button" className="tb-brand" onClick={onGoHome} aria-label="Knotarium — go to the dashboard">
        <span className="tb-mark" aria-hidden="true"><Activity size={18} /></span>
        <span className="tb-word">
          <span className="tb-word-name">KNOT<span className="tb-word-dim">ARIUM</span><span className="tb-word-dot">.</span></span>
          {version && (
            <span className="tb-build" title={version.buildTimeUtc ? `Built ${new Date(version.buildTimeUtc).toLocaleString()}` : undefined}>
              v{version.version}{version.buildTimeUtc ? ` · built ${formatBuildTime(version.buildTimeUtc)}` : ''}
            </span>
          )}
        </span>
      </button>

      <nav
        className="tb-nav"
        aria-label="Primary"
        ref={navRef}
        onKeyDown={onNavKeyDown}
      >
        {rows}
      </nav>

      <div className="tb-spacer" ref={spacerRef} aria-hidden="true" />

      <button
        type="button"
        className="tb-cmd"
        onClick={() => setPaletteOpen(true)}
        aria-label="Search — Ctrl K"
        aria-keyshortcuts="Control+K"
        {...tipProps('Search — Ctrl K', true)}
      >
        <Search size={15} />
        <span className="tb-cmd-text">Search…</span>
        <span className="tb-cmd-kbd">Ctrl K</span>
      </button>

      <div className="tb-right">
        <RuntimeArmingToggle armed={armed} busy={armingBusy} onToggle={() => onSetArmed(!(armed === true))} />
        <span className="tb-vr" aria-hidden="true" />
        <span className="tb-clock"><Clock /></span>
        {/* Offline help, served from wwwroot/help by the same process. A plain link rather than a
            router push: the docs are static HTML outside the SPA, and opening them in a new tab
            means reading them does not discard canvas state. Absolute "/help/" (not "help/") so it
            resolves the same from every screen. */}
        <a
          href="/help/"
          target="_blank"
          rel="noopener noreferrer"
          className="tb-gbtn sq"
          aria-label="Open the documentation"
          {...tipProps('Help', true)}
        >
          <BookOpen size={16} />
        </a>
        {authenticated
          ? <UserMenu onOpenTour={onOpenTour} localTime={<Clock />} />
          : (
            // No account button to hold it: keep the tour reachable on its own.
            <button type="button" className="tb-gbtn sq" onClick={onOpenTour} aria-label="Take the product tour" {...tipProps('Product tour', true)}>
              <Compass size={16} />
            </button>
          )}
      </div>

      {tip && createPortal(
        <div className="tb-tip" role="tooltip" style={{ left: tip.x, top: tip.y, transform: 'translateX(-50%)' }}>
          {tip.text}
        </div>,
        document.body,
      )}

      {paletteOpen && (
        <CommandPalette
          onClose={() => setPaletteOpen(false)}
          entries={paletteEntries}
          onOpenWorkflow={onOpenWorkflow}
          onOpenRun={onOpenRun}
        />
      )}
    </header>
  );
}
