// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ReactFlowProvider, applyNodeChanges } from '@xyflow/react';
import type { Edge as RFEdge, Node as RFNode, NodeChange } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { api } from '../../utils/api';
import { createNodePackageMetadataMap, enrichNodesWithPackageMetadata, applySubflowNames } from '../../utils/nodePackages';
import { schemaMapper } from '../../utils/schemaMapper';
import { decodeNodeStatus } from '../../utils/executionStatus';
import { replaceIfChanged } from '../../utils/stableState';
import type { ExecutionInstance, ExecutionJournal, NodePackageSummary, NodeStatus, ReplayResult, WorkflowDefinition, WorkflowScheduleSummary, WorkflowVersionSummary } from '../../types';
import { createNodeTypes } from '../../utils/nodeTypes';
import { ExecutionCanvasPanel } from './ExecutionCanvasPanel';
import { ExecutionSidebar } from './ExecutionSidebar';
import { ReplayDialog } from './ReplayDialog';
import { TimeTravelInspector, type InspectorStep } from './TimeTravelInspector';
import type { ExecutionDetailProps } from './types';
import { ErrorLineageBanner } from './ErrorLineageBanner';
import { useHandlerRun } from './useHandlerRun';
import { ErrorHandlerCardNode } from './ErrorHandlerCardNode';
import { useVariableStore } from '../../stores/useVariableStore';
import {
  buildJournalOverview,
  formatClockTime,
  formatDuration,
  getDurationBetween,
  getDeviceEventProvenance,
  getLatestPendingAttemptId,
  getStatusFromJournal,
  getTimelineSummaryStatus,
  isProgressiveTransition,
  isSkippedData,
  isTerminalExecutionStatus,
  mapExecutionStatus,
  mapNodeStatus,
  normalizeStatusValue,
  normalizeTriggerOrigin,
} from './timelineUtils';

// Derive a node's execution status for canvas painting, honouring device-event provenance: the origin
// block reads "Triggered", and once the run is terminal, branches this event didn't fire read "Skipped"
// instead of a phantom "Pending" (see getNodeTimelineStatus for the timeline-side equivalent).
function deriveCanvasNodeStatus(
  nodeId: string,
  state: { status: unknown; outputs?: Record<string, unknown> } | undefined,
  exec: ExecutionInstance,
): string {
  if (state) {
    // state.status arrives as a numeric enum ordinal (the API has no string-enum converter); decode it
    // to the name mapNodeStatus/normalizeStatusValue expect, else every node falls through to 'Pending'
    // and the canvas never colours. See utils/executionStatus.
    return isSkippedData(state.outputs) ? 'Skipped' : mapNodeStatus(decodeNodeStatus(state.status) as NodeStatus, exec.status);
  }
  const device = getDeviceEventProvenance(exec);
  if (device) {
    if (device.sourceNodeId && nodeId === device.sourceNodeId) {
      return 'Triggered';
    }
    if (isTerminalExecutionStatus(exec.status)) {
      return 'Skipped';
    }
  }
  return 'Pending';
}

function mapWorkflowVersionToDefinition(version: { workflowDefinitionId: { value: string }; nodes: WorkflowDefinition['nodes']; edges: WorkflowDefinition['edges'] }): WorkflowDefinition {
  return {
    id: version.workflowDefinitionId,
    name: 'Workflow',
    nodes: version.nodes,
    edges: version.edges,
  };
}

function ExecutionDetailInner({ executionId, onBack, onTriggeredExecution, onGrantFileAccess }: ExecutionDetailProps) {
  const [nodes, setNodes] = useState<RFNode[]>([]);
  const [edges, setEdges] = useState<RFEdge[]>([]);
  const [workflow, setWorkflow] = useState<WorkflowDefinition | null>(null);
  const [execution, setExecution] = useState<ExecutionInstance | null>(null);
  const [journal, setJournal] = useState<ExecutionJournal[]>([]);
  const [workflowSchedules, setWorkflowSchedules] = useState<WorkflowScheduleSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [availableNodes, setAvailableNodes] = useState<NodePackageSummary[]>([]);
  // id -> name for every workflow, so subflow nodes show the workflow they call in the run view too.
  const [workflowNameById, setWorkflowNameById] = useState<Record<string, string>>({});
  const [manualDecisionNodeId, setManualDecisionNodeId] = useState<string | null>(null);
  const [triggeringScheduleNodeId, setTriggeringScheduleNodeId] = useState<string | null>(null);
  const [workflowVersions, setWorkflowVersions] = useState<WorkflowVersionSummary[]>([]);
  const [replayNodeId, setReplayNodeId] = useState<string | null>(null);
  const [replayBusy, setReplayBusy] = useState(false);
  const [replayResult, setReplayResult] = useState<ReplayResult | null>(null);
  const [replayError, setReplayError] = useState<string | null>(null);
  const [stepMode, setStepMode] = useState(false);
  const [stepIndex, setStepIndex] = useState(0);
  const [groupExpansionState, setGroupExpansionState] = useState<{
    executionId: string;
    expandedKeys: Set<string>;
    collapsedFailedKeys: Set<string>;
  }>({
    executionId,
    expandedKeys: new Set(),
    collapsedFailedKeys: new Set(),
  });

  const availableNodeMetadata = useMemo(
    () => createNodePackageMetadataMap(availableNodes),
    [availableNodes],
  );

  const consoleEndRef = useRef<HTMLDivElement>(null);
  const lastForwardedExecutionIdRef = useRef<string | null>(null);
  const actionNodes = execution?.nodeStates?.filter((state) => state.status === 'RequiresManualDecision') ?? [];

  const refreshWorkflowSchedules = useCallback(async (workflowId: string) => {
    try {
      const schedules = await api.getWorkflowSchedules(workflowId);
      setWorkflowSchedules(replaceIfChanged(schedules));
    } catch (error) {
      console.error('Error loading workflow schedules:', error);
      setWorkflowSchedules(replaceIfChanged<WorkflowScheduleSummary[]>([]));
    }
  }, []);

  useEffect(() => {
    consoleEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [journal]);

  // Refresh node execution statuses from a fresh execution snapshot WITHOUT rebuilding the graph.
  // The node set and layout are fixed for a run (established once on load), so we update statuses in
  // place — preserving each node's identity, position, and the current viewport. Rebuilding from the
  // definition here (as this used to) reset React Flow's fit/pan on every SSE event and on "Replay
  // Logs", making the graph appear to vanish, and also wiped any manual drag positions. The `wf`
  // parameter is kept for call-site compatibility but no longer needed.
  const syncNodeStates = useCallback((_wf: WorkflowDefinition, exec: ExecutionInstance) => {
    setNodes((currentNodes) => currentNodes.map((node) => {
      const state = exec.nodeStates?.find((ns) => ns.nodeId.value === node.id);
      const dbStatus = deriveCanvasNodeStatus(node.id, state, exec);
      const localStatus = (node.data?.execStatus as string) || 'Pending';
      const finalStatus = isProgressiveTransition(localStatus, dbStatus as string) ? dbStatus : localStatus;
      const errorMessage = state?.errorMessage || node.data?.errorMessage;

      return {
        ...node,
        execStatus: finalStatus,
        errorMessage,
        data: {
          ...node.data,
          execStatus: finalStatus,
          errorMessage,
        },
      } as RFNode;
    }));
  }, []);

  useEffect(() => {
    const loadAll = async () => {
      setLoading(true);

      try {
        const exec = await api.getExecution(executionId);
        setExecution(exec);
        useVariableStore.getState().updateVariableValues(exec.workflowDefinitionId.value, exec);

        let wf = await api.getWorkflow(exec.workflowDefinitionId.value);

        if (exec.workflowVersionId) {
          // The list endpoint is metadata-only now, so fetch the full payload of the
          // pinned version from the detail endpoint to render its exact graph.
          const [versions, matchingVersion] = await Promise.all([
            api.getWorkflowVersions(exec.workflowDefinitionId.value),
            api
              .getWorkflowVersionDetail(exec.workflowDefinitionId.value, exec.workflowVersionId)
              .catch(() => null),
          ]);
          setWorkflowVersions(versions);

          if (matchingVersion) {
            wf = mapWorkflowVersionToDefinition(matchingVersion);
          }
        }

        setWorkflow(wf);
        await refreshWorkflowSchedules(exec.workflowDefinitionId.value);

        const journalEntries = await api.getExecutionJournal(executionId);
        setJournal(journalEntries);

        let metadataMap: ReturnType<typeof createNodePackageMetadataMap> = {};

        try {
          const packages = await api.getNodePackages();
          setAvailableNodes(packages);
          metadataMap = createNodePackageMetadataMap(packages);
        } catch (packageError) {
          console.error('Error loading node packages:', packageError);
        }

        try {
          const workflowList = await api.getWorkflows();
          setWorkflowNameById(Object.fromEntries(workflowList.map((w) => [w.id.value, w.name])));
        } catch (workflowError) {
          console.error('Error loading workflow names:', workflowError);
        }

        const { nodes: rfNodes, edges: rfEdges } = schemaMapper.toReactFlow(wf);
        const enrichedRfNodes = enrichNodesWithPackageMetadata(rfNodes, metadataMap);
        const baselineNodes = enrichedRfNodes.map((rfNode) => {
          const state = exec.nodeStates?.find((ns) => ns.nodeId.value === rfNode.id);
          const dbStatus = deriveCanvasNodeStatus(rfNode.id, state, exec);
          const journalStatus = getStatusFromJournal(rfNode.id, journalEntries);
          const finalStatus = isProgressiveTransition(dbStatus, journalStatus) ? journalStatus : dbStatus;
          const errorMessage = state?.errorMessage || (finalStatus === 'Failed' ? 'Execution failed' : undefined);

          return {
            ...rfNode,
            execStatus: finalStatus,
            errorMessage,
            data: {
              ...rfNode.data,
              execStatus: finalStatus,
              errorMessage,
            },
          } as RFNode;
        });

        setNodes(baselineNodes);
        setEdges(rfEdges);
      } catch (error) {
        console.error(error);
      } finally {
        setLoading(false);
      }
    };

    void loadAll();
  }, [executionId, refreshWorkflowSchedules]);

  useEffect(() => {
    lastForwardedExecutionIdRef.current = executionId;
  }, [executionId]);

  useEffect(() => {
    if (!execution?.workflowDefinitionId?.value) {
      return;
    }

    const workflowId = execution.workflowDefinitionId.value;
    const timer = setInterval(() => {
      void refreshWorkflowSchedules(workflowId);
    }, 5000);

    return () => {
      clearInterval(timer);
    };
  }, [execution?.workflowDefinitionId?.value, refreshWorkflowSchedules]);

  useEffect(() => {
    if (!execution?.workflowDefinitionId?.value) {
      return;
    }

    const workflowId = execution.workflowDefinitionId.value;
    const currentExecutionCreatedAt = execution.createdAt ? new Date(execution.createdAt).getTime() : 0;
    let isCancelled = false;

    const followLatestScheduledExecution = async () => {
      try {
        const executions = await api.getExecutions();
        if (isCancelled) {
          return;
        }

        const nextScheduledExecution = executions.find((candidate) => {
          if (candidate.id === executionId) {
            return false;
          }

          if (candidate.workflowDefinitionId?.value !== workflowId) {
            return false;
          }

          // Follow automatically-triggered runs the user didn't start by hand: scheduled runs and
          // device-event runs (a wired device event firing). Manual/webhook runs are not auto-followed.
          if (candidate.triggerOrigin !== 'schedule' && candidate.triggerOrigin !== 'deviceEvent') {
            return false;
          }

          const candidateCreatedAt = candidate.createdAt ? new Date(candidate.createdAt).getTime() : 0;
          return candidateCreatedAt > currentExecutionCreatedAt;
        });

        if (!nextScheduledExecution) {
          return;
        }

        if (lastForwardedExecutionIdRef.current === nextScheduledExecution.id) {
          return;
        }

        lastForwardedExecutionIdRef.current = nextScheduledExecution.id;
        onTriggeredExecution(nextScheduledExecution.id);
      } catch (error) {
        console.error('Error loading latest workflow executions:', error);
      }
    };

    const timer = setInterval(() => {
      void followLatestScheduledExecution();
    }, 5000);

    return () => {
      isCancelled = true;
      clearInterval(timer);
    };
  }, [execution?.createdAt, execution?.workflowDefinitionId?.value, executionId, onTriggeredExecution]);

  useEffect(() => {
    const sseUrl = api.getSseUrl(executionId);
    const EventSourceCtor = globalThis.EventSource;
    if (!EventSourceCtor) {
      return;
    }

    const eventSource = new EventSourceCtor(sseUrl);

    const handleEvent = (event: MessageEvent) => {
      try {
        const rawEntry = JSON.parse(event.data);
        const logEntry = api.mapJournalEntry(rawEntry);

        setJournal((previous) => {
          if (previous.some((item) => item.id === logEntry.id)) {
            return previous;
          }

          return [...previous, logEntry].sort((left, right) => new Date(left.timestamp).getTime() - new Date(right.timestamp).getTime());
        });

        if (logEntry.nodeId) {
          const nodeId = logEntry.nodeId.value;
          let sseStatus: string | null = null;
          let errorMessage: string | undefined;

          if (logEntry.eventType === 'NodeExecutionStarted') {
            sseStatus = 'Running';
          } else if (logEntry.eventType === 'NodeExecutionCompleted' || logEntry.eventType === 'NodeResumed') {
            sseStatus = 'Completed';
          } else if (logEntry.eventType === 'NodeExecutionFailed') {
            sseStatus = 'Failed';
            errorMessage = typeof logEntry.data?.error === 'string' ? logEntry.data.error : 'Execution failed';
          } else if (logEntry.eventType === 'WorkflowSuspended') {
            sseStatus = 'Waiting';
          }

          if (sseStatus) {
            setNodes((currentNodes) => currentNodes.map((node) => {
              if (node.id !== nodeId) {
                return node;
              }

              const currentStatus = (node.data?.execStatus as string) || 'Pending';
              if (!isProgressiveTransition(currentStatus, sseStatus as string)) {
                return node;
              }

              return {
                ...node,
                execStatus: sseStatus,
                errorMessage,
                data: {
                  ...node.data,
                  execStatus: sseStatus,
                  errorMessage,
                },
              } as RFNode;
            }));
          }
        }

        void api.getExecution(executionId).then((updatedExecution) => {
          setExecution(updatedExecution);
          if (workflow) {
            syncNodeStates(workflow, updatedExecution);
          }
          useVariableStore.getState().updateVariableValues(updatedExecution.workflowDefinitionId.value, updatedExecution);
        }).catch(() => {});
      } catch (error) {
        console.error('Failed to parse SSE event payload:', error);
      }
    };

    eventSource.addEventListener('WorkflowStarted', handleEvent);
    eventSource.addEventListener('NodeExecutionStarted', handleEvent);
    eventSource.addEventListener('NodeExecutionCompleted', handleEvent);
    eventSource.addEventListener('NodeExecutionFailed', handleEvent);
    eventSource.addEventListener('NodeResumed', handleEvent);
    eventSource.addEventListener('WorkflowSuspended', handleEvent);
    eventSource.addEventListener('WorkflowCompleted', handleEvent);
    eventSource.addEventListener('WorkflowFailed', handleEvent);

    return () => {
      eventSource.close();
    };
  }, [executionId, syncNodeStates, workflow]);

  const handleReplay = useCallback(async () => {
    try {
      const logs = await api.getExecutionJournal(executionId);
      setJournal(logs);

      const exec = await api.getExecution(executionId);
      setExecution(exec);
      await refreshWorkflowSchedules(exec.workflowDefinitionId.value);

      if (workflow) {
        syncNodeStates(workflow, exec);
      }
      useVariableStore.getState().updateVariableValues(exec.workflowDefinitionId.value, exec);
    } catch {
      return;
    }
  }, [executionId, refreshWorkflowSchedules, syncNodeStates, workflow]);

  const handleFireSchedule = useCallback(async (scheduleNodeId: string) => {
    if (!execution) {
      return;
    }

    setTriggeringScheduleNodeId(scheduleNodeId);
    try {
      const instance = await api.fireWorkflowSchedule(execution.workflowDefinitionId.value, scheduleNodeId);
      onTriggeredExecution(instance.id);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to fire scheduler.';
      alert(message);
    } finally {
      setTriggeringScheduleNodeId(null);
    }
  }, [execution, onTriggeredExecution]);

  const handleManualDecision = useCallback(async (nodeId: string, decision: 'Retry' | 'Skip' | 'Fail') => {
    setManualDecisionNodeId(nodeId);

    try {
      await api.applyManualDecision(
        executionId,
        nodeId,
        decision,
        `Operator selected ${decision.toLowerCase()} from execution detail.`,
        getLatestPendingAttemptId(nodeId, journal),
      );

      await handleReplay();
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to record manual decision.';
      alert(message);
    } finally {
      setManualDecisionNodeId(null);
    }
  }, [executionId, handleReplay, journal]);

  const handleNodeReplayRequest = useCallback(async (nodeId: string) => {
    setReplayResult(null);
    setReplayError(null);
    setReplayNodeId(nodeId);

    // Lazily load published versions for the target-version selector if we don't have them yet.
    if (workflowVersions.length === 0 && execution?.workflowDefinitionId?.value) {
      try {
        const versions = await api.getWorkflowVersions(execution.workflowDefinitionId.value);
        setWorkflowVersions(versions);
      } catch (error) {
        console.error('Error loading workflow versions:', error);
      }
    }
  }, [execution?.workflowDefinitionId?.value, workflowVersions.length]);

  const handleConfirmReplay = useCallback(async (options: { targetVersionId?: string; mockSideEffects: boolean }) => {
    if (!replayNodeId) {
      return;
    }

    setReplayBusy(true);
    setReplayError(null);

    try {
      const result = await api.replayExecution(executionId, replayNodeId, options);
      setReplayResult(result);

      // No side-effect warnings: jump straight to the new run.
      if (result.warnings.length === 0) {
        setReplayNodeId(null);
        onTriggeredExecution(result.newExecutionId);
      }
    } catch (error) {
      setReplayError(error instanceof Error ? error.message : 'Failed to start replay.');
    } finally {
      setReplayBusy(false);
    }
  }, [executionId, onTriggeredExecution, replayNodeId]);

  const handleCloseReplay = useCallback(() => {
    setReplayNodeId(null);
    setReplayResult(null);
    setReplayError(null);
  }, []);

  const handleOpenReplayRun = useCallback((newExecutionId: string) => {
    setReplayNodeId(null);
    setReplayResult(null);
    onTriggeredExecution(newExecutionId);
  }, [onTriggeredExecution]);

  const executionVisualStatus = execution ? mapExecutionStatus(execution.status) : null;
  // Apply React Flow's own node changes (chiefly measured dimensions once it lays the nodes out). Without
  // an onNodesChange handler the canvas is fully controlled and React Flow can't persist those dimensions,
  // so it re-initializes on every status update — which resets the viewport and makes the graph appear to
  // vanish ("blinks then empty"). Position changes are ignored (the run view isn't a place to rearrange).
  const onNodesChange = useCallback((changes: NodeChange<RFNode>[]) => {
    const persisted = changes.filter((change) => change.type === 'dimensions' || change.type === 'select');
    if (persisted.length === 0) {
      return;
    }
    setNodes((current) => applyNodeChanges(persisted, current));
  }, []);

  // Memoized so unrelated re-renders (execution/journal updates during a run) don't hand React Flow a
  // brand-new node array every time — which churns the canvas and can disturb the viewport.
  const displayNodes = useMemo(
    () => applySubflowNames(enrichNodesWithPackageMetadata(nodes, availableNodeMetadata), workflowNameById),
    [nodes, availableNodeMetadata, workflowNameById],
  );
  const availableNodeIds = useMemo(
    () => Array.from(new Set([...availableNodes.map((nodePackage) => nodePackage.id), ...displayNodes.map((node) => node.type || '')].filter(Boolean))),
    [availableNodes, displayNodes],
  );

  const combinedNodeTypes = useMemo(
    () => createNodeTypes(availableNodeIds),
    [availableNodeIds],
  );
  const journalOverview = useMemo(
    () => buildJournalOverview(journal, workflow, availableNodeMetadata, execution),
    [availableNodeMetadata, execution, journal, workflow],
  );
  const failedGroupKeys = useMemo(
    () => new Set(
      journalOverview
        .filter((group) => normalizeStatusValue(group.status) === 'Failed')
        .map((group) => group.key),
    ),
    [journalOverview],
  );

  // Each executed node becomes a step in the time-travel inspector, in execution order.
  const inspectorSteps = useMemo<InspectorStep[]>(
    () => journalOverview
      .filter((group) => !group.isWorkflow && group.nodeId)
      .map((group) => ({
        key: group.key,
        nodeId: group.nodeId as string,
        title: group.title,
        status: group.status,
        durationLabel: group.durationLabel,
      })),
    [journalOverview],
  );

  const handleToggleStepThrough = useCallback(() => {
    setStepMode((current) => {
      const next = !current;
      if (next) {
        // Open on the first failed step when there is one, else the first step.
        const failedIndex = inspectorSteps.findIndex((step) => normalizeStatusValue(step.status) === 'Failed');
        setStepIndex(failedIndex >= 0 ? failedIndex : 0);
      }
      return next;
    });
  }, [inspectorSteps]);

  // When opening a run that is ALREADY finished, start in Step Through so the execution view reads as a
  // distinct time-travel review rather than a static copy of the editor canvas (which now looks the same,
  // just status-coloured). A live run the user is watching stays in the live view — we record the decision
  // once per run on open, so a later completion doesn't yank it into step mode, and a manual toggle sticks.
  const autoStepDecidedForRef = useRef<string | null>(null);
  useEffect(() => {
    if (autoStepDecidedForRef.current === executionId) return;
    if (!execution || execution.id !== executionId) return;
    if (!isTerminalExecutionStatus(execution.status)) {
      autoStepDecidedForRef.current = executionId; // live/in-progress at open — never auto-step this run
      return;
    }
    if (inspectorSteps.length === 0) return; // wait until the per-node steps are built from the journal
    autoStepDecidedForRef.current = executionId;
    const failedIndex = inspectorSteps.findIndex((step) => normalizeStatusValue(step.status) === 'Failed');
    setStepIndex(failedIndex >= 0 ? failedIndex : 0);
    setStepMode(true);
  }, [execution, executionId, inspectorSteps]);

  const clampedStepIndex = Math.min(stepIndex, Math.max(inspectorSteps.length - 1, 0));
  const stepHighlightNodeId = stepMode ? inspectorSteps[clampedStepIndex]?.nodeId : undefined;
  // Nodes reached only after the current step are the "future" — dim them so the canvas
  // conveys where you are in time, not just "everything ran".
  const stepFutureNodeIds = stepMode
    ? inspectorSteps.slice(clampedStepIndex + 1).map((step) => step.nodeId)
    : undefined;

  const handleSelectStepNode = useCallback((nodeId: string) => {
    const index = inspectorSteps.findIndex((step) => step.nodeId === nodeId);
    if (index >= 0) {
      setStepIndex(index);
    }
  }, [inspectorSteps]);
  const expandedGroupKeys = useMemo(() => {
    const effectiveExpandedKeys = groupExpansionState.executionId === executionId
      ? groupExpansionState.expandedKeys
      : new Set<string>();
    const effectiveCollapsedFailedKeys = groupExpansionState.executionId === executionId
      ? groupExpansionState.collapsedFailedKeys
      : new Set<string>();
    const next = new Set<string>();

    for (const key of failedGroupKeys) {
      if (!effectiveCollapsedFailedKeys.has(key)) {
        next.add(key);
      }
    }

    for (const key of effectiveExpandedKeys) {
      if (next.has(key)) {
        next.delete(key);
      } else {
        next.add(key);
      }
    }

    return next;
  }, [executionId, failedGroupKeys, groupExpansionState.collapsedFailedKeys, groupExpansionState.executionId, groupExpansionState.expandedKeys]);
  const timelineSummaryStatus = useMemo(
    () => getTimelineSummaryStatus(executionVisualStatus, journalOverview),
    [executionVisualStatus, journalOverview],
  );

  // Gate on the timeline summary status, not execution.status: the detail endpoint serializes the
  // status enum as a number (e.g. 5), which normalizes to 'Pending' on the client — only the
  // journal-derived timelineSummaryStatus reliably reports 'Failed'.
  const handlerRun = useHandlerRun(executionId, timelineSummaryStatus ?? undefined);

  // Affordance A — the "catch branch": a synthetic amber card wired from the failed node, so the
  // cause→handler relationship is visible on the canvas itself.
  const failedBranchNodeId = useMemo(
    () => journalOverview.find((group) => normalizeStatusValue(group.status) === 'Failed')?.nodeId,
    [journalOverview],
  );
  const canvasNodes = useMemo<RFNode[]>(() => {
    if (!handlerRun || !failedBranchNodeId) return displayNodes;
    const failed = displayNodes.find((node) => node.id === failedBranchNodeId);
    if (!failed) return displayNodes;
    const card: RFNode = {
      id: '__errorHandlerCard',
      type: 'errorHandlerCard',
      position: { x: (failed.position?.x ?? 0) + 30, y: (failed.position?.y ?? 0) + 250 },
      draggable: false,
      selectable: false,
      data: { status: handlerRun.status, onOpen: () => onTriggeredExecution(handlerRun.id) },
    };
    return [...displayNodes, card];
  }, [displayNodes, handlerRun, failedBranchNodeId, onTriggeredExecution]);
  const canvasEdges = useMemo<RFEdge[]>(() => {
    if (!handlerRun || !failedBranchNodeId) return edges;
    const failed = displayNodes.find((node) => node.id === failedBranchNodeId);
    if (!failed) return edges;
    const sourceHandle = (failed.data?.outputHandles as string[] | undefined)?.[0] ?? 'result';
    const branchEdge: RFEdge = {
      id: '__errorHandlerEdge',
      source: failedBranchNodeId,
      sourceHandle,
      target: '__errorHandlerCard',
      animated: true,
      style: { stroke: '#f5a623', strokeWidth: 2, strokeDasharray: '6 5' },
    };
    return [...edges, branchEdge];
  }, [edges, displayNodes, handlerRun, failedBranchNodeId]);
  const canvasNodeTypes = useMemo(
    () => ({ ...combinedNodeTypes, errorHandlerCard: ErrorHandlerCardNode }),
    [combinedNodeTypes],
  );

  const nodeTimelineCount = journalOverview.length;
  const timelineStartTimestamp = journal[0]?.timestamp || execution?.createdAt;
  const timelineEndTimestamp = journal[journal.length - 1]?.timestamp || execution?.updatedAt || execution?.createdAt;
  const timelineDurationLabel = formatDuration(getDurationBetween(timelineStartTimestamp, timelineEndTimestamp));
  const runStartedLabel = formatClockTime(timelineStartTimestamp);
  const triggerOrigin = normalizeTriggerOrigin(execution?.triggerOrigin);
  const triggerSchedule = triggerOrigin === 'schedule'
    ? workflowSchedules.find((schedule) => journalOverview.some((group) => group.nodeId === schedule.nodeId)) ?? workflowSchedules[0]
    : undefined;
  // A device-event run carries the inbound signal under the `signal` global; its `kind` tells whether
  // it was an event or an incoming action, so the pill reads "ACTION" vs "EVENT" accordingly (the
  // backend origin is the same "deviceEvent" for both).
  const deviceSignalKind = (() => {
    const s = execution?.globalVariables?.signal as { kind?: unknown } | undefined;
    return s && typeof s === 'object' && s.kind === 'action' ? 'action' : 'event';
  })();
  const triggerPillLabel = triggerOrigin === 'schedule'
    ? 'AUTO'
    : triggerOrigin === 'deviceEvent'
      ? (deviceSignalKind === 'action' ? 'ACTION' : 'EVENT')
      : 'MANUAL';
  const triggerDescription = triggerOrigin === 'schedule'
    ? `Triggered by schedule${triggerSchedule ? ` · ${triggerSchedule.cronExpression} · next ${formatClockTime(triggerSchedule.nextFireAtUtc) ?? triggerSchedule.nextFireAtUtc}` : ''}`
    : triggerOrigin === 'deviceEvent'
      ? `Triggered by a device ${deviceSignalKind}${runStartedLabel ? ` · ${runStartedLabel}` : ''}`
      : `Fired manually - "Fire now"${runStartedLabel ? ` · ${runStartedLabel}` : ''}`;

  const toggleGroupExpansion = useCallback((groupKey: string) => {
    setGroupExpansionState((current) => {
      const expandedKeys = current.executionId === executionId ? new Set(current.expandedKeys) : new Set<string>();
      const collapsedFailedKeys = current.executionId === executionId ? new Set(current.collapsedFailedKeys) : new Set<string>();

      if (failedGroupKeys.has(groupKey)) {
        if (collapsedFailedKeys.has(groupKey)) {
          collapsedFailedKeys.delete(groupKey);
        } else {
          collapsedFailedKeys.add(groupKey);
        }
      } else if (expandedKeys.has(groupKey)) {
        expandedKeys.delete(groupKey);
      } else {
        expandedKeys.add(groupKey);
      }

      return {
        executionId,
        expandedKeys,
        collapsedFailedKeys,
      };
    });
  }, [executionId, failedGroupKeys]);

  return (
    <div style={{ display: 'flex', height: '100%', width: '100%', background: '#060a10', position: 'relative' }}>
      <ErrorLineageBanner
        errorOfExecutionId={execution?.errorOfExecutionId}
        handlerRun={handlerRun}
        onOpen={onTriggeredExecution}
        onOpenHandler={() => { if (handlerRun) onTriggeredExecution(handlerRun.id); }}
      />
      <style>{`
        @keyframes execution-timeline-pulse {
          0%, 100% { box-shadow: 0 0 0 0 rgba(34, 211, 238, 0.32); }
          70% { box-shadow: 0 0 0 10px rgba(34, 211, 238, 0); }
        }

        @keyframes execution-timeline-spin {
          from { transform: rotate(0deg); }
          to { transform: rotate(360deg); }
        }

        @keyframes execution-timeline-dash {
          from { background-position: 0 0; }
          to { background-position: 0 24px; }
        }

        .node-row:hover .chev-btn {
          background: rgba(59, 158, 255, 0.14);
          border-color: rgba(59, 158, 255, 0.5);
          color: #8fd3ff;
        }

        .chev-btn {
          display: grid;
          place-items: center;
          width: 26px;
          height: 26px;
          border-radius: 8px;
          background: rgba(255, 255, 255, 0.05);
          border: 1px solid #283246;
          color: #aab6c4;
          transition: background .15s, border-color .15s, color .15s;
          flex: 0 0 26px;
        }

        .chev-btn.open {
          background: rgba(59, 158, 255, 0.14);
          border-color: rgba(59, 158, 255, 0.4);
          color: #8fd3ff;
        }

        .chev {
          transition: transform .18s ease;
        }

        .chev.open {
          transform: rotate(180deg);
        }
      `}</style>

      <ExecutionCanvasPanel
        executionId={executionId}
        workflowName={workflow?.name ?? (execution ? workflowNameById[execution.workflowDefinitionId.value] : undefined)}
        executionVisualStatus={executionVisualStatus}
        loading={loading}
        nodes={canvasNodes}
        edges={canvasEdges}
        combinedNodeTypes={canvasNodeTypes}
        onNodesChange={onNodesChange}
        onBack={onBack}
        onReplay={handleReplay}
        onNodeReplayRequest={handleNodeReplayRequest}
        lineage={execution?.replayOfExecutionId ? {
          sourceExecutionId: execution.replayOfExecutionId,
          fromNodeId: execution.replayFromNodeId,
        } : undefined}
        onOpenExecution={onTriggeredExecution}
        stepThroughActive={stepMode}
        onToggleStepThrough={inspectorSteps.length > 0 ? handleToggleStepThrough : undefined}
        highlightedNodeId={stepHighlightNodeId}
        dimmedNodeIds={stepFutureNodeIds}
        inspectorSlot={stepMode && inspectorSteps.length > 0 ? (
          <TimeTravelInspector
            steps={inspectorSteps}
            nodeStates={execution?.nodeStates ?? []}
            index={stepIndex}
            onIndexChange={setStepIndex}
            onClose={() => setStepMode(false)}
          />
        ) : null}
      />

      {replayNodeId && (
        <ReplayDialog
          nodeId={replayNodeId}
          originalVersionId={execution?.workflowVersionId}
          versions={workflowVersions}
          busy={replayBusy}
          result={replayResult}
          error={replayError}
          onConfirm={handleConfirmReplay}
          onClose={handleCloseReplay}
          onOpenRun={handleOpenReplayRun}
        />
      )}

      <ExecutionSidebar
        execution={execution}
        workflowSchedules={workflowSchedules}
        actionNodes={actionNodes}
        manualDecisionNodeId={manualDecisionNodeId}
        triggeringScheduleNodeId={triggeringScheduleNodeId}
        journal={journal}
        journalOverview={journalOverview}
        timelineSummaryStatus={timelineSummaryStatus}
        executionVisualStatus={executionVisualStatus}
        nodeTimelineCount={nodeTimelineCount}
        timelineDurationLabel={timelineDurationLabel}
        triggerOrigin={triggerOrigin}
        triggerPillLabel={triggerPillLabel}
        triggerDescription={triggerDescription}
        expandedGroupKeys={expandedGroupKeys}
        consoleEndRef={consoleEndRef}
        onFireSchedule={handleFireSchedule}
        onManualDecision={handleManualDecision}
        onToggleGroupExpansion={toggleGroupExpansion}
        activeStepNodeId={stepHighlightNodeId}
        onSelectStepNode={stepMode ? handleSelectStepNode : undefined}
        handlerRun={handlerRun}
        onOpenHandler={() => { if (handlerRun) onTriggeredExecution(handlerRun.id); }}
        onGrantFileAccess={onGrantFileAccess}
      />
    </div>
  );
}

export function ExecutionDetail(props: ExecutionDetailProps) {
  return (
    <ReactFlowProvider>
      <ExecutionDetailInner {...props} />
    </ReactFlowProvider>
  );
}