// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Search } from 'lucide-react';
import { useScrimClose } from '../../hooks/useScrimClose';
import { api } from '../../utils/api';
import type { ExecutionInstance, WorkflowDefinition } from '../../types';

/**
 * ⌘K / Ctrl+K palette.
 *
 * The adaptive bar is only honest if search is complete: once labels are gone,
 * this is the fallback that still reaches everything by NAME. It therefore
 * covers all destinations (with synonyms — "logs" finds the Execution
 * Visualizer), every workflow, recent runs by partial id or workflow name, and
 * the handful of actions that are not destinations.
 */

export interface PaletteEntry {
  id: string;
  group: 'Destinations' | 'Actions' | 'Workflows' | 'Runs';
  label: string;
  /** Right-aligned context (a run's status, a workflow's state, a hint). */
  hint?: string;
  icon?: ReactNode;
  /** Extra search terms; never displayed. */
  synonyms?: string[];
  run: () => void;
}

interface CommandPaletteProps {
  /** Mounted only while open, so every opening starts on a clean query. */
  onClose: () => void;
  /** Destinations + actions supplied by the bar; workflows and runs are loaded here. */
  entries: PaletteEntry[];
  onOpenWorkflow: (workflowId: string) => void;
  onOpenRun: (executionId: string) => void;
}

const GROUP_ORDER: PaletteEntry['group'][] = ['Destinations', 'Actions', 'Workflows', 'Runs'];

function matches(entry: PaletteEntry, query: string): boolean {
  if (!query) return true;
  const haystack = [entry.label, entry.hint ?? '', ...(entry.synonyms ?? [])].join(' ').toLowerCase();
  // Every whitespace-separated term must appear somewhere, so "dead runs" and
  // "runs dead" behave the same.
  return query.split(/\s+/).filter(Boolean).every((term) => haystack.includes(term));
}

export function CommandPalette({ onClose, entries, onOpenWorkflow, onOpenRun }: CommandPaletteProps) {
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState(0);
  const [workflows, setWorkflows] = useState<WorkflowDefinition[]>([]);
  const [runs, setRuns] = useState<ExecutionInstance[]>([]);
  const listRef = useRef<HTMLDivElement | null>(null);
  const onScrim = useScrimClose(onClose);

  // Load the searchable content once per opening. Failures degrade to
  // destinations + actions only, which is still a complete navigation surface.
  useEffect(() => {
    let cancelled = false;
    void api.getWorkflows()
      .then((list) => { if (!cancelled) setWorkflows(list); })
      .catch(() => { if (!cancelled) setWorkflows([]); });
    void api.getExecutions()
      .then((list) => { if (!cancelled) setRuns(list); })
      .catch(() => { if (!cancelled) setRuns([]); });
    return () => { cancelled = true; };
  }, []);

  const all = useMemo<PaletteEntry[]>(() => {
    const workflowEntries: PaletteEntry[] = workflows.map((workflow) => ({
      id: `wf:${workflow.id.value}`,
      group: 'Workflows',
      label: workflow.name,
      hint: workflow.isEnabled === false ? 'inactive' : undefined,
      synonyms: ['workflow', 'open', 'edit', workflow.id.value],
      run: () => onOpenWorkflow(workflow.id.value),
    }));

    const runEntries: PaletteEntry[] = [...runs]
      .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())
      .slice(0, 40)
      .map((run) => ({
        id: `run:${run.id}`,
        group: 'Runs',
        label: run.workflowName ? `${run.workflowName} · ${run.id.slice(0, 8)}` : run.id,
        hint: run.status,
        // The full id so pasting any fragment of it finds the run.
        synonyms: ['run', 'execution', run.id, run.status],
        run: () => onOpenRun(run.id),
      }));

    return [...entries, ...workflowEntries, ...runEntries];
  }, [entries, workflows, runs, onOpenWorkflow, onOpenRun]);

  const results = useMemo(() => {
    const q = query.trim().toLowerCase();
    const hits = all.filter((entry) => matches(entry, q));
    return GROUP_ORDER.flatMap((group) => hits.filter((entry) => entry.group === group));
  }, [all, query]);

  // Keep the active row in view while arrowing through a long list.
  useEffect(() => {
    const row = listRef.current?.querySelector<HTMLElement>('.tb-pal-row.sel');
    // Optional call: jsdom has no scrollIntoView, and this is pure polish.
    row?.scrollIntoView?.({ block: 'nearest' });
  }, [selected, results]);

  const commit = (entry: PaletteEntry | undefined) => {
    if (!entry) return;
    onClose();
    entry.run();
  };

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setSelected((i) => (results.length === 0 ? 0 : (i + 1) % results.length));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setSelected((i) => (results.length === 0 ? 0 : (i - 1 + results.length) % results.length));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      commit(results[selected]);
    }
  };

  return createPortal(
    <div className="tb-pal-scrim" onMouseDown={onScrim} role="presentation">
      <div className="tb-pal" role="dialog" aria-modal="true" aria-label="Command palette">
        <div className="tb-pal-field">
          <Search size={17} />
          <input
            autoFocus
            value={query}
            onChange={(e) => { setQuery(e.target.value); setSelected(0); }}
            onKeyDown={onKeyDown}
            placeholder="Go to a screen, a workflow, a run — or type an action…"
            aria-label="Search destinations, workflows, runs and actions"
            aria-autocomplete="list"
          />
        </div>
        <div className="tb-pal-list" ref={listRef} role="listbox" aria-label="Results">
          {results.length === 0 && <div className="tb-pal-empty">Nothing matches “{query}”.</div>}
          {results.map((entry, index) => {
            // Results are already ordered by group, so a heading is due
            // wherever the group differs from the previous row's.
            const head = index === 0 || results[index - 1].group !== entry.group ? entry.group : null;
            return (
              <div key={entry.id}>
                {head && <div className="tb-pal-group">{head}</div>}
                <button
                  type="button"
                  role="option"
                  aria-selected={index === selected}
                  className={`tb-pal-row${index === selected ? ' sel' : ''}`}
                  onMouseEnter={() => setSelected(index)}
                  onClick={() => commit(entry)}
                >
                  {entry.icon}
                  <span>{entry.label}</span>
                  {entry.hint && <span className="tb-pal-hint">{entry.hint}</span>}
                </button>
              </div>
            );
          })}
        </div>
        <div className="tb-pal-foot">
          <span>↑↓ navigate</span>
          <span>↵ open</span>
          <span>esc close</span>
        </div>
      </div>
    </div>,
    document.body,
  );
}
