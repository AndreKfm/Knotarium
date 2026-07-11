import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../utils/api';
import type { OptionItem, ParameterDefinition } from '../types';

export type NodeOptionsState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'ready'; options: OptionItem[]; hasMore: boolean }
  | { status: 'empty' }
  | { status: 'error'; code: string; message: string };

interface UseNodeOptionsArgs {
  param: ParameterDefinition;
  /** Sibling property values of the same node — source of dependsOn parent values. */
  properties: Record<string, unknown>;
  /** Stored server-config / connection id, resolved by the caller. */
  connectionId?: string | null;
  /** Gate loading until the dropdown is actually opened. */
  enabled: boolean;
  /** Server-side search term (debounced before it triggers a reload). */
  search?: string;
}

function toStringValue(value: unknown): string {
  if (value == null) return '';
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  // A persisted dynamic-option object stores its stable key under `value`.
  if (typeof value === 'object' && 'value' in (value as Record<string, unknown>)) {
    return String((value as Record<string, unknown>).value ?? '');
  }
  return '';
}

/**
 * State machine for a dynamic-options parameter: idle → loading → (ready | empty | error).
 * Loads when {@link UseNodeOptionsArgs.enabled} becomes true and whenever the resolved
 * dependsOn values, connection, or debounced search change. Exposes {@link reload} for the
 * manual refresh button.
 */
export function useNodeOptions({ param, properties, connectionId, enabled, search }: UseNodeOptionsArgs) {
  const [state, setState] = useState<NodeOptionsState>({ status: 'idle' });

  // Resolve the dependsOn dict: static loaderConfig merged with live sibling values.
  const dependsOn = useMemo(() => {
    const merged: Record<string, string> = { ...(param.loaderConfig ?? {}) };
    for (const name of param.dependsOn ?? []) {
      merged[name] = toStringValue(properties[name]);
    }
    return merged;
  }, [param.loaderConfig, param.dependsOn, properties]);

  // Stable dependency key so the effect only re-runs on a real change.
  const dependsOnKey = useMemo(() => JSON.stringify(dependsOn), [dependsOn]);

  // Debounce the search term so typing doesn't fire a request per keystroke.
  const [debouncedSearch, setDebouncedSearch] = useState(search ?? '');
  useEffect(() => {
    const handle = setTimeout(() => setDebouncedSearch(search ?? ''), 300);
    return () => clearTimeout(handle);
  }, [search]);

  // Guards against out-of-order responses overwriting newer state.
  const requestSeq = useRef(0);

  const load = useCallback(async (refresh = false) => {
    if (!param.optionsLoader) {
      setState({ status: 'error', code: 'MISCONFIGURED', message: 'No options loader configured for this field.' });
      return;
    }

    const seq = ++requestSeq.current;
    setState({ status: 'loading' });
    try {
      const result = await api.loadNodeOptions(
        param.integrationType ?? 'generic',
        param.optionsLoader,
        { connectionId, dependsOn, search: debouncedSearch || undefined },
        refresh,
      );
      if (seq !== requestSeq.current) return; // superseded
      if (result.error) {
        setState({ status: 'error', code: result.error.code, message: result.error.message });
        return;
      }
      if (!result.options || result.options.length === 0) {
        setState({ status: 'empty' });
        return;
      }
      setState({ status: 'ready', options: result.options, hasMore: result.hasMore });
    } catch (err) {
      if (seq !== requestSeq.current) return;
      setState({ status: 'error', code: 'REQUEST_FAILED', message: err instanceof Error ? err.message : 'Failed to load options.' });
    }
  }, [param.optionsLoader, param.integrationType, connectionId, dependsOn, debouncedSearch]);

  useEffect(() => {
    if (!enabled) return;
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, connectionId, dependsOnKey, debouncedSearch]);

  // The manual refresh button busts the server cache; automatic loads use the cache.
  const reload = useCallback(() => load(true), [load]);

  return { state, reload };
}
