import { describe, it, expect, beforeEach } from 'vitest';
import { useSubflowOpenStore } from './useSubflowOpenStore';

describe('useSubflowOpenStore', () => {
  beforeEach(() => useSubflowOpenStore.getState().clearRequest());

  it('starts with no pending request', () => {
    expect(useSubflowOpenStore.getState().requestNodeId).toBeNull();
  });

  it('records an open request by node id', () => {
    useSubflowOpenStore.getState().requestOpen('node-7');
    expect(useSubflowOpenStore.getState().requestNodeId).toBe('node-7');
  });

  it('clears the request', () => {
    useSubflowOpenStore.getState().requestOpen('node-7');
    useSubflowOpenStore.getState().clearRequest();
    expect(useSubflowOpenStore.getState().requestNodeId).toBeNull();
  });

  it('notifies subscribers when a request is posted', () => {
    const seen: (string | null)[] = [];
    const unsub = useSubflowOpenStore.subscribe((s) => seen.push(s.requestNodeId));
    useSubflowOpenStore.getState().requestOpen('abc');
    unsub();
    expect(seen).toContain('abc');
  });
});
