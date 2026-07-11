# Step 17 — B5.2: Runs Filter & Timeline

## Goal
Redesign the flat execution runs list in `Dashboard.tsx` into a modern operations panel. The dashboard must support filtering by execution status—including the newly introduced `Retrying` state (mapped backend-side from `ExecutionStatus.WaitingForRetry`)—label runs clearly with their trigger origin metadata (Manual, Webhook, Schedule), and bucket execution list items into readable date timeline groups.

---

## Invariant Alignment
* **Flat Log to Dashboard**: Transition from a raw database printout list into a professional operations management canvas.
* **Trigger-origin observability**: Differentiate scheduled system runs, webhooks, and manual clicks.
* **Retrying Filter Integration**: Ensure `Retrying` is mapped in UI filter lists, matching backend `ExecutionStatus.WaitingForRetry` values.

---

## Proposed Changes

### 1. Modify [Dashboard.tsx](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/components/Dashboard.tsx) [MODIFY]
* **Filter Panel**: Insert a horizontal filter strip at the top containing a status selection dropdown (`All`, `Running`, `Waiting`, `Retrying`, `Completed`, `Failed`, `Cancelled`) and a search filter input.
* **Retrying State Mapping**: Map backend status `WaitingForRetry` to the `"Retrying"` UI label and dropdown filter selection value.
* **Trigger Origins**: Read the execution instance trigger origin property (e.g. `manual`, `webhook`, `scheduler`) and render corresponding small icons/badges (e.g., `⚡ Manual`, `🌐 Webhook`, `📅 Schedule`).
* **Timeline Grouping**: Group runs in memory by date before rendering them:
```typescript
const groupRunsByDate = (runs: ExecutionRun[]) => {
  const groups: Record<string, ExecutionRun[]> = { Today: [], Yesterday: [], 'Last 7 Days': [], Older: [] };
  // Date evaluation and sorting logic...
  return groups;
};
```

### 2. Update [api.ts](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/api.ts) [MODIFY]
* Update fetch calls for executions to optionally accept query filters:
```typescript
export const getExecutions = async (filters?: { status?: string; search?: string }) => {
  // Pass status (including "WaitingForRetry") and search filters to backend /api/executions
};
```

---

## Verification & Test Checklist

### 1. Component Tests
* Write a React component unit test in `Dashboard.test.tsx` verifying:
  * **Retrying Filter Visibility**: Render the filter dropdown select and verify `Retrying` is present. Set filter status to `Retrying` and assert that a mock run containing status `"WaitingForRetry"` is successfully filtered and rendered.
  * **Timeline Buckets**: Given list items with timestamps matching today, yesterday, and 10 days ago, assert they render inside separate headed list wrappers (`Today`, `Yesterday`, `Older`).

### 2. Manual Verification
* Trigger a backoff retry, open the dashboard, select the `"Retrying"` filter, and confirm it isolates the pending retry run.
