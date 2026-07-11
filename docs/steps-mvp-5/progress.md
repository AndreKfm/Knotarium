# Knotarium MVP 5 — Implementation Steps Progress

This folder contains the step-by-step implementation guide for building Knotarium MVP 5 (Path B & UI Ergonomics). Each step is fully documented in its own dedicated markdown file to maintain modularity, clear design boundaries, and track progress effectively.

---

## 📊 Phase Progress Tracker (Backend-First Build Order)

| Step | Headline | Target Files | Status | Test Status |
| :--- | :--- | :--- | :---: | :---: |
| **Step 1** | [B2.1: Enums & Journal Schema](./B2_1_Execution_Enums_Journal_Schema.md) | `ExecutionStatus.cs`, `NodeExecutionStatus.cs` | `[x] Completed` | `🟢 Green` |
| **Step 2** | [B3.1: Manifest Policy Parser](./B3_1_Manifest_Policy_Parser.md) | `NodePackageManifest.cs`, `WorkflowCompiler.cs` | `[x] Completed` | `🟢 Green` |
| **Step 3** | [B4.1: Scheduler Trigger Registry](./B4_1_Scheduler_Trigger_Registry.md) | `scheduler/manifest.yaml`, `WorkflowCompiler.cs` | `[x] Completed` | `🟢 Green` |
| **Step 4** | [B2.2: Transactional Suspension](./B2_2_Transactional_Suspension.md) | `WorkflowExecutor.cs`, `JournalFoldService.cs` | `[x] Completed` | `🟢 Green` |
| **Step 5** | [B2.3: Secure Correlation Tokens](./B2_3_Secure_Correlation_Tokens.md) | `CorrelationToken.cs`, `AppDbContext.cs` | `[x] Completed` | `🟢 Green` |
| **Step 6** | [B2.4: Resume API Gateway](./B2_4_Resume_API_Gateway.md) | `ExecutionsController.cs`, `WorkflowExecutor.cs` | `[x] Completed` | `🟢 Green` |
| **Step 7** | [B2.5: Execution Work Item Processor](./B2_5_Execution_Work_Item_Processor.md) | `ExecutionWorker.cs`, `WorkflowExecutor.cs` | `[x] Completed` | `🟢 Green` |
| **Step 8** | [B3.2: Gated Auto-Retry Traversal](./B3_2_Gated_Auto_Retry.md) | `WorkflowExecutor.cs` | `[x] Completed` | `🟢 Green` |
| **Step 9** | [B3.3: Persistent Retry State](./B3_3_Persistent_Retry_State.md) | `ExecutionInstance.cs`, `AppDbContext.cs` | `[x] Completed` | `🟢 Green` |
| **Step 10** | [B3.4: Pre-Execution Markers](./B3_4_Pre_Execution_Markers.md) | `WorkflowExecutor.cs`, `RecoveryService.cs` | `[x] Completed` | `🟢 Green` |
| **Step 11** | [B4.2: Schedule Fires Idempotency](./B4_2_Schedule_Fires_Idempotency.md) | `ScheduleFire.cs`, `AppDbContext.cs`, `WorkflowEnqueueService.cs` | `[x] Completed` | `🟢 Green` |
| **Step 12** | [B4.3: Scheduling Worker Service](./B4_3_Scheduling_Worker_Service.md) | `SchedulingWorker.cs`, `ScheduleEvaluationService.cs` | `[x] Completed` | `🟢 Green` |
| **Step 13** | [B1.3: Unified Node Registry](./B1_3_Unified_Node_Registry.md) | `DbNodePackageManifestProvider.cs`, `Canvas.tsx`, `api.ts` | `[x] Completed` | `🟢 Green` |
| **Step 14** | [B1.1: Sidebar Palette Layout](./B1_1_Sidebar_Layout.md) | `Canvas.tsx`, `SidebarPalette.tsx` | `[x] Completed` | `🟢 Green` |
| **Step 15** | [B1.2: Pinned & Recent State](./B1_2_Pinned_Recent_State.md) | `Canvas.tsx`, `useCanvasStore.ts` | `[x] Completed` | `🟢 Green` |
| **Step 16** | [B5.1: Observability Badges UI](./B5_1_Dashboard_Observability_Badges.md) | `ExecutionDetail.tsx`, `ExecutionsController.cs` | `[x] Completed` | `🟢 Green` |
| **Step 17** | [B5.2: Runs Filter & Timeline](./B5_2_Runs_Filter_Timeline.md) | `Dashboard.tsx`, `api.ts` | `[x] Completed` | `🟢 Green` |

---

## Current Focus

- **Recently completed:** [B5.2: Runs Filter & Timeline](./B5_2_Runs_Filter_Timeline.md) adds the dashboard filter strip, Retrying state mapping, trigger-origin badges, and date-bucketed execution timeline rendering.
- **Current milestone:** All 17 MVP 5 implementation steps are now completed and verified green across the documented execution surfaces.
- **Follow-on milestone:** Architecture document rolled forward and synchronized in [Knotarium_MVP_Architecture-5.md](../Knotarium_MVP_Architecture-5.md); MVP 5 documentation close-out is complete.

---

## 🛠️ Verification Guidelines

- **Pending (`[ ] Pending` / `🔴 Pending`)**: Development has not initiated, and associated tests are not verified.
- **In Progress (`[/] In Progress` / `🟡 In Progress`)**: Component-level changes are active, and tests are being drafted or debugged.
- **Completed & Green (`[x] Completed` / `🟢 Green`)**: Code is finalized, compiles successfully, and all associated unit, integration, and UI tests pass with 100% success.

> [!IMPORTANT]
> All 17 implementation steps are now **Completed & Green**, and the main architectural specification document [Knotarium_MVP_Architecture-5.md](../Knotarium_MVP_Architecture-5.md) has been updated and marked complete.
