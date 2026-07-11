import { describe, expect, it } from 'vitest';
import type { ExecutionInstance, WorkflowDefinition } from '../types';
import {
  buildJournalOverview,
  getDeviceEventProvenance,
  getTimelineSummaryStatus,
  isTerminalExecutionStatus,
} from '../components/ExecutionDetail/timelineUtils';

// A device block ("External Device") with two event pins, each wired to its own Log node. One event fires
// (log-a), so only that branch runs; the device block never executes as a work node and log-b never runs.
const workflow = {
  id: { value: 'wf-device' },
  name: 'Device Events',
  nodes: [
    { id: { value: 'externalDevice-1' }, type: 'externalDevice', properties: {} },
    { id: { value: 'log-a' }, type: 'log', properties: {} },
    { id: { value: 'log-b' }, type: 'log', properties: {} },
  ],
  edges: [
    { from: { value: 'externalDevice-1' }, to: { value: 'log-a' }, output: 'event:1:started', input: 'in' },
    { from: { value: 'externalDevice-1' }, to: { value: 'log-b' }, output: 'event:2:started', input: 'in' },
  ],
} as unknown as WorkflowDefinition;

function deviceExecution(status: string): ExecutionInstance {
  return {
    id: { value: 'exec-1' },
    status,
    workflowDefinitionId: { value: 'wf-device' },
    triggerOrigin: 'deviceEvent',
    createdAt: '2026-07-06T12:50:59Z',
    globalVariables: {
      __deviceEventSourceNode: 'externalDevice-1',
      __deviceEventFiredPin: 'Event 1 ▸ Started',
    },
    // Only the fired branch ran.
    nodeStates: [
      { nodeId: { value: 'log-a' }, status: 'Completed', outputs: {}, inputs: {}, executionCount: 1 },
    ],
  } as unknown as ExecutionInstance;
}

describe('device-event timeline provenance', () => {
  it('reads the source node + fired pin from run globals (device-event runs only)', () => {
    const provenance = getDeviceEventProvenance(deviceExecution('Completed'));
    expect(provenance).toEqual({ sourceNodeId: 'externalDevice-1', firedPin: 'Event 1 ▸ Started' });

    const nonDevice = { ...deviceExecution('Completed'), triggerOrigin: 'schedule' } as ExecutionInstance;
    expect(getDeviceEventProvenance(nonDevice)).toBeNull();
  });

  it('marks the origin Triggered and the un-fired branch Skipped once the run is terminal', () => {
    const groups = buildJournalOverview([], workflow, {}, deviceExecution('Completed'));
    const byId = Object.fromEntries(groups.map((g) => [g.nodeId, g]));

    expect(byId['externalDevice-1'].status).toBe('Triggered');
    expect(byId['externalDevice-1'].hint).toBe('Triggered · Event 1 ▸ Started');
    expect(byId['log-a'].status).toBe('Completed');
    expect(byId['log-b'].status).toBe('Skipped');

    // The origin is t0 of the run, so it sorts to the very top — ahead of the +9ms fired branch.
    expect(groups[0].nodeId).toBe('externalDevice-1');
  });

  it('leaves un-fired branches Pending while the run is still in flight', () => {
    const groups = buildJournalOverview([], workflow, {}, deviceExecution('Running'));
    const byId = Object.fromEntries(groups.map((g) => [g.nodeId, g]));

    // Origin is the trigger source regardless of run state; the branch is not yet decided.
    expect(byId['externalDevice-1'].status).toBe('Triggered');
    expect(byId['log-b'].status).toBe('Pending');
  });

  it('summarises a finished device run as Completed (origin Triggered + branch Skipped)', () => {
    const groups = buildJournalOverview([], workflow, {}, deviceExecution('Completed'));
    // Groups are [Triggered, Completed, Skipped] — none Pending — so the run reads Completed, not a
    // phantom Pending. (getTimelineSummaryStatus folds Triggered/Skipped in alongside Completed.)
    const statuses = groups.map((g) => g.status).sort();
    expect(statuses).toEqual(['Completed', 'Skipped', 'Triggered']);
    expect(getTimelineSummaryStatus('Completed', groups)).toBe('Completed');
  });

  it('treats terminal states as terminal for skip derivation (string and numeric enum forms)', () => {
    expect(isTerminalExecutionStatus('Completed')).toBe(true);
    expect(isTerminalExecutionStatus('Failed')).toBe(true);
    expect(isTerminalExecutionStatus('Running')).toBe(false);
    expect(isTerminalExecutionStatus('Pending')).toBe(false);
    // The detail endpoint serializes status as a number: Cancelled(3), Completed(4), Failed(5), Discarded(7).
    expect(isTerminalExecutionStatus(4)).toBe(true); // Completed
    expect(isTerminalExecutionStatus(5)).toBe(true); // Failed
    expect(isTerminalExecutionStatus(3)).toBe(true); // Cancelled
    expect(isTerminalExecutionStatus(0)).toBe(false); // Pending
    expect(isTerminalExecutionStatus(1)).toBe(false); // Running
    expect(isTerminalExecutionStatus(2)).toBe(false); // Suspended
  });

  it('skips un-fired branches when the run status arrives as the numeric enum (detail endpoint)', () => {
    // execStatus 4 = Completed, but serialized as a number — the case that previously left branches Pending.
    const exec = { ...deviceExecution('Completed'), status: 4 } as unknown as ExecutionInstance;
    const groups = buildJournalOverview([], workflow, {}, exec);
    const byId = Object.fromEntries(groups.map((g) => [g.nodeId, g]));
    expect(byId['externalDevice-1'].status).toBe('Triggered');
    expect(byId['log-b'].status).toBe('Skipped');
  });
});
