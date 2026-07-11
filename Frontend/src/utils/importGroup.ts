import { api, ApiError } from './api';
import type { WorkflowGroup } from '../types';

const IMPORTED_GROUP_NAME = 'Imported';
const IMPORTED_GROUP_COLOR = '#8b7cf0';

const findImported = (groups: WorkflowGroup[]) =>
  groups.find((g) => g.name.toLowerCase() === IMPORTED_GROUP_NAME.toLowerCase());

/**
 * Returns the "Imported" group's id, creating it if absent. `saveGroups` is optimistic-concurrency
 * (ETag), so a concurrent import / group edit can lose the race — retry once: re-fetch (a peer may
 * have just created the group), then try again. Keeps parallel imports from spawning duplicate groups.
 */
async function resolveImportedGroupId(): Promise<string> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const { container, etag } = await api.getGroups();
    const existing = findImported(container.groups);
    if (existing) return existing.id;

    const group: WorkflowGroup = {
      id: 'grp_' + Math.random().toString(36).substring(2, 11), // mirrors the Dashboard's id scheme
      name: IMPORTED_GROUP_NAME,
      color: IMPORTED_GROUP_COLOR,
    };
    try {
      await api.saveGroups({ version: container.version ?? 1, groups: [...container.groups, group] }, etag);
      return group.id;
    } catch (err) {
      // ETag conflict (a concurrent change) → loop once more and re-read before giving up.
      if (err instanceof ApiError && (err.status === 412 || err.status === 409) && attempt === 0) {
        continue;
      }
      throw err;
    }
  }
  // Final read after the retry exhausted: a peer must have created it by now.
  const { container } = await api.getGroups();
  const settled = findImported(container.groups);
  if (settled) return settled.id;
  throw new Error('Could not resolve the "Imported" group after a concurrency retry.');
}

/**
 * Files a freshly-imported workflow into a shared "Imported" group so template instances are bundled
 * instead of scattered through "Ungrouped". Best-effort: any failure is logged and swallowed — the
 * import already succeeded, the workflow is just left ungrouped. Reuses the exact endpoints the
 * Dashboard's group dropdown uses (getGroups/saveGroups + updateWorkflow metadata).
 */
export async function ensureImportedGroup(workflowId: string): Promise<void> {
  try {
    const groupId = await resolveImportedGroupId();
    const workflow = await api.getWorkflow(workflowId);
    await api.updateWorkflow(workflowId, {
      ...workflow,
      metadata: { ...(workflow.metadata ?? {}), group: groupId },
    });
  } catch (err) {
    console.warn('Could not file the imported workflow into the "Imported" group:', err);
  }
}
