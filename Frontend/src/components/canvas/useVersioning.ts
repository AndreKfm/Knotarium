import { useCallback, useEffect, useRef, useState } from 'react'
import type { Dispatch, RefObject, SetStateAction } from 'react'
import type { Edge, Node as RFNode } from '@xyflow/react'
import { api } from '../../utils/api'
import { schemaMapper } from '../../utils/schemaMapper'
import { enrichNodesWithPackageMetadata, type NodePackageMetadata } from '../../utils/nodePackages'
import { applyGroupCollapseOnLoad } from '../../node-editor/nodeGroup'
import { diffVersions, type DiffablePayload, type VersionDiff } from '../../utils/versionDiff'
import {
  DRAFT_MODE,
  editorModeReducer,
  isEditingDisabled,
  type EditorMode,
} from '../../node-editor/editorMode'
import { isApiError, getErrorMessage, getErrorDiagnostics } from '../../utils/apiErrors'
import { useWorkflowVersions } from '../../hooks/useWorkflowVersions'
import type {
  ActiveWorkflowVersion,
  RestoreVersionResult,
  WorkflowVersion,
  WorkflowVersionSummary,
} from '../../types'

export interface UseVersioningArgs {
  currentId: string
  workflowName: string
  nodesRef: RefObject<RFNode[]>
  edgesRef: RefObject<Edge[]>
  availableNodeMetadataRef: RefObject<Record<string, NodePackageMetadata>>
  setWorkflowStatusMessage: Dispatch<SetStateAction<string | null>>
}

/**
 * Version history, read-only preview, diff, and restore for the open workflow. Extracted from
 * Canvas.tsx. The version data is disjoint from the live nodes/edges — this hook never mutates the
 * working draft; entering a read-only mode just makes Canvas render previewNodes/previewEdges
 * instead. The three toolbar setters (workflow versions / active / selected) are exposed so the
 * load effect and Save/Publish in Canvas can write them.
 */
export function useVersioning(args: UseVersioningArgs) {
  const { currentId, workflowName, nodesRef, edgesRef, availableNodeMetadataRef, setWorkflowStatusMessage } = args

  // Toolbar activate-flow state (written by Canvas's load effect + Save/Publish too).
  const [workflowVersions, setWorkflowVersions] = useState<WorkflowVersionSummary[]>([])
  const [activeWorkflowVersion, setActiveWorkflowVersion] = useState<ActiveWorkflowVersion | null>(null)
  const activeWorkflowVersionRef = useRef<ActiveWorkflowVersion | null>(null)
  useEffect(() => { activeWorkflowVersionRef.current = activeWorkflowVersion }, [activeWorkflowVersion])
  const [selectedActivationVersionId, setSelectedActivationVersionId] = useState('')

  // ── Version history drawer (Ctrl/⌘ + Shift + H) ──
  const [historyOpen, setHistoryOpen] = useState(false)
  const historyOpenRef = useRef(historyOpen)
  historyOpenRef.current = historyOpen
  // Fetch version metadata only while the drawer is open. The drawer shares no state with the
  // toolbar's `workflowVersions` (which drives the activate flow).
  const {
    versions: historyVersions,
    loading: historyLoading,
    error: historyError,
    refresh: refreshHistory,
  } = useWorkflowVersions(currentId || undefined, historyOpen)

  // ── Editor-mode state machine ──
  // Draft = live editable graph; PublishedPreview / Diff = read-only snapshots.
  const [editorMode, setEditorMode] = useState<EditorMode>(DRAFT_MODE)
  const dispatchMode = useCallback((action: Parameters<typeof editorModeReducer>[1]) => {
    setEditorMode((prev) => editorModeReducer(prev, action))
  }, [])
  const readOnly = isEditingDisabled(editorMode)
  // Read-only canvas state shown during preview (separate from the live draft).
  const [previewNodes, setPreviewNodes] = useState<RFNode[]>([])
  const [previewEdges, setPreviewEdges] = useState<Edge[]>([])
  const [previewVersionNumber, setPreviewVersionNumber] = useState<number | null>(null)
  // Restore dialog state.
  const [restoreTarget, setRestoreTarget] = useState<WorkflowVersionSummary | null>(null)
  const [restoreBusy, setRestoreBusy] = useState(false)
  const [restoreError, setRestoreError] = useState<string | null>(null)
  const [restoreResult, setRestoreResult] = useState<RestoreVersionResult | null>(null)
  // Diff view state (left → right). `diff` is computed by the pure versionDiff module.
  const [diffState, setDiffState] = useState<{ leftLabel: string; rightLabel: string; diff: VersionDiff } | null>(null)

  const versionToPayload = useCallback((version: WorkflowVersion): DiffablePayload => {
    return { nodes: version.nodes, edges: version.edges }
  }, [])

  // Switching workflows must drop any in-flight preview/diff so a stale read-only snapshot can't
  // leak across workflows.
  useEffect(() => {
    setEditorMode(DRAFT_MODE)
    setPreviewNodes([])
    setPreviewEdges([])
    setPreviewVersionNumber(null)
    setDiffState(null)
    setRestoreTarget(null)
  }, [currentId])

  // The live working draft as a DiffablePayload, derived through the same backend mapper used for
  // save/publish so the comparison is apples-to-apples.
  const draftPayload = useCallback((): DiffablePayload => {
    const def = schemaMapper.toBackend(currentId, workflowName, nodesRef.current, edgesRef.current)
    return { nodes: def.nodes, edges: def.edges }
  }, [currentId, workflowName, nodesRef, edgesRef])

  // Enter read-only preview of a committed version.
  const handlePreviewVersion = useCallback(async (versionId: string) => {
    if (!currentId) return
    try {
      const version = await api.getWorkflowVersionDetail(currentId, versionId)
      const def = { id: { value: currentId }, name: workflowName, nodes: version.nodes, edges: version.edges }
      const { nodes: rfNodes, edges: rfEdges } = schemaMapper.toReactFlow(def)
      setPreviewNodes(applyGroupCollapseOnLoad(enrichNodesWithPackageMetadata(rfNodes, availableNodeMetadataRef.current)))
      setPreviewEdges(rfEdges)
      setPreviewVersionNumber(version.versionNumber)
      dispatchMode({ type: 'openPreview', versionId })
    } catch (err) {
      setWorkflowStatusMessage(`Could not load version for preview: ${getErrorMessage(err, 'Unknown error')}`)
    }
  }, [currentId, workflowName, dispatchMode, availableNodeMetadataRef, setWorkflowStatusMessage])

  // Exit any read-only mode → live draft is rendered again (never mutated).
  const exitReadOnly = useCallback(() => {
    dispatchMode({ type: 'exit' })
    setPreviewNodes([])
    setPreviewEdges([])
    setPreviewVersionNumber(null)
    setDiffState(null)
  }, [dispatchMode])

  // The history drawer and a read-only preview are one "version overview" — leaving either returns
  // you to the draft in a single action.
  const closeVersionOverview = useCallback(() => {
    setHistoryOpen(false)
    exitReadOnly()
  }, [exitReadOnly])

  // Runtime dropdown selection → preview the chosen version read-only. Selecting the active version
  // returns to the live editable draft. Activation never happens on select.
  const handleSelectVersion = useCallback((versionId: string) => {
    setSelectedActivationVersionId(versionId)
    if (!versionId || versionId === activeWorkflowVersion?.workflowVersionId) {
      exitReadOnly()
      return
    }
    void handlePreviewVersion(versionId)
  }, [activeWorkflowVersion, exitReadOnly, handlePreviewVersion])

  // Transient preview while the user lingers on a version in the runtime dropdown.
  const handleHoverPreviewVersion = useCallback((versionId: string | null) => {
    if (!versionId) {
      handleSelectVersion(selectedActivationVersionId)
      return
    }
    if (versionId === activeWorkflowVersion?.workflowVersionId) {
      exitReadOnly()
      return
    }
    void handlePreviewVersion(versionId)
  }, [selectedActivationVersionId, activeWorkflowVersion, handleSelectVersion, exitReadOnly, handlePreviewVersion])

  // Diff a committed version against the working draft (committed = left, draft = right).
  const handleDiffAgainstDraft = useCallback(async (versionId: string) => {
    if (!currentId) return
    try {
      const version = await api.getWorkflowVersionDetail(currentId, versionId)
      const diff = diffVersions(versionToPayload(version), draftPayload())
      setDiffState({ leftLabel: `v${version.versionNumber}`, rightLabel: 'working draft', diff })
      dispatchMode({ type: 'openDiff', leftVersionId: versionId, rightVersionId: 'draft' })
    } catch (err) {
      setWorkflowStatusMessage(`Could not load version for diff: ${getErrorMessage(err, 'Unknown error')}`)
    }
  }, [currentId, versionToPayload, draftPayload, dispatchMode, setWorkflowStatusMessage])

  // The most-wanted diff: working draft vs the active version.
  const handleDiffDraftVsActive = useCallback(async () => {
    if (!currentId) return
    const activeId = activeWorkflowVersionRef.current?.workflowVersionId
    if (!activeId) {
      setWorkflowStatusMessage('No active version to diff against — publish one first.')
      return
    }
    try {
      const version = await api.getWorkflowVersionDetail(currentId, activeId)
      const diff = diffVersions(versionToPayload(version), draftPayload())
      setDiffState({ leftLabel: `active v${version.versionNumber}`, rightLabel: 'working draft', diff })
      dispatchMode({ type: 'openDiff', leftVersionId: activeId, rightVersionId: 'draft' })
    } catch (err) {
      setWorkflowStatusMessage(`Could not load active version for diff: ${getErrorMessage(err, 'Unknown error')}`)
    }
  }, [currentId, versionToPayload, draftPayload, dispatchMode, setWorkflowStatusMessage])

  // Open the restore confirmation for a version id (resolves its summary for the dialog).
  const openRestoreDialog = useCallback((versionId: string) => {
    const summary =
      historyVersions.find((v) => v.id === versionId) ||
      workflowVersions.find((v) => v.id === versionId) ||
      null
    setRestoreResult(null)
    setRestoreError(null)
    setRestoreTarget(summary ?? { id: versionId, versionNumber: 0, createdAt: '', createdBy: null, label: null, origin: 'Published', isActive: false, restoredFromVersionId: null, nodeCount: 0, executionCount: 0 })
  }, [historyVersions, workflowVersions])

  const confirmRestore = useCallback(async ({ activate }: { activate: boolean }) => {
    if (!currentId || !restoreTarget) return
    setRestoreBusy(true)
    setRestoreError(null)
    try {
      const result = await api.restoreVersion(currentId, restoreTarget.id, activate)
      setRestoreResult(result)
      // Refresh the panel list + active badge so the new forward copy shows up.
      void refreshHistory()
      const [versions, activeVersion] = await Promise.all([
        api.getWorkflowVersions(currentId),
        api.getActiveWorkflowVersion(currentId),
      ])
      setWorkflowVersions(versions)
      setSelectedActivationVersionId(activeVersion?.workflowVersionId ?? versions[0]?.id ?? '')
      setActiveWorkflowVersion(activeVersion)
    } catch (err) {
      const errorDiagnostics = getErrorDiagnostics(err)
      if (isApiError(err) && err.status === 400 && errorDiagnostics && errorDiagnostics.length > 0) {
        setRestoreError(`Activation failed — fix these first: ${errorDiagnostics.map((d) => `[${d.code}] ${d.message}`).join('; ')}`)
      } else if (isApiError(err) && err.status === 409) {
        setRestoreError('Another activation happened concurrently. Reopen and try again.')
      } else {
        setRestoreError(getErrorMessage(err, 'Restore failed.'))
      }
    } finally {
      setRestoreBusy(false)
    }
  }, [currentId, restoreTarget, refreshHistory])

  return {
    // toolbar activate-flow state + setters (Canvas load/save/run write these)
    workflowVersions, setWorkflowVersions,
    activeWorkflowVersion, setActiveWorkflowVersion, activeWorkflowVersionRef,
    selectedActivationVersionId, setSelectedActivationVersionId,
    // history drawer
    historyOpen, setHistoryOpen, historyOpenRef,
    historyVersions, historyLoading, historyError, refreshHistory,
    // editor mode + preview
    editorMode, dispatchMode, readOnly,
    previewNodes, previewEdges, previewVersionNumber,
    // restore + diff
    restoreTarget, setRestoreTarget, restoreBusy, restoreError, setRestoreError,
    restoreResult, setRestoreResult, diffState,
    // handlers
    handlePreviewVersion, exitReadOnly, closeVersionOverview,
    handleSelectVersion, handleHoverPreviewVersion,
    handleDiffAgainstDraft, handleDiffDraftVsActive,
    openRestoreDialog, confirmRestore,
  }
}
