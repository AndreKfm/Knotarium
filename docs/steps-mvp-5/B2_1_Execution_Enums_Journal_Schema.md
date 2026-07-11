# Step 1 — B2.1: Enums & Journal Schema

## Goal
Implement a strict separation between run-wide execution status (`ExecutionStatus`) and granular node-level status (`NodeExecutionStatus`). To prevent state representation ambiguity, we are adding `WaitingForRetry` to the run status. To prevent EF Core database integer column shift corruption when migrating data, we must assign **explicit integer mapping values** to both enums. Additionally, update the `ExecutionJournal` model schema to support versioned payload records.

---

## Invariant Alignment
* **Invariant 2.1 (Journal Versioning):** Journal payloads must carry a schema version property (`v1`, `v2`, etc.) to ensure backward compatibility and clean parsing.
* **Explicit Enum Mapping**: Both `ExecutionStatus` and `NodeExecutionStatus` carry explicit integer keys. This isolates status changes from index-shifting database bugs.

### Backend-to-UI Status Mappings
To ensure premium, filter-friendly operability on the dashboard:
* Run-status `Suspended` -> UI label: **Waiting**
* Run-status `WaitingForRetry` -> UI label: **Retrying**
* Node-status `RequiresManualDecision` -> UI label: **Manual decision required**

---

## Proposed Changes

### 1. Update/Define Status Enums [MODIFY]
In core C# models:
* Define run-level `ExecutionStatus`:
```csharp
public enum ExecutionStatus
{
    Pending = 0,
    Running = 1,
    Suspended = 2,
    Cancelled = 3,
    Completed = 4,
    Failed = 5,
    WaitingForRetry = 6 // Appended with explicit ID to prevent data shifts
}
```
* Define node-level `NodeExecutionStatus` (Must Fix — migration safety):
```csharp
public enum NodeExecutionStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Retrying = 4,
    RequiresManualDecision = 5 // Appended with explicit ID to prevent data shifts
}
```

### 2. Update [ExecutionJournal.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Core/ExecutionJournal.cs) [MODIFY]
Ensure the journal record supports `PayloadVersion` defaults:
```csharp
public record JournalEntry(
    Guid Id,
    Guid ExecutionInstanceId,
    string EventType,
    string Payload,
    string PayloadVersion = "v2",
    DateTime CreatedAtUtc = default
);
```

---

## Verification & Test Checklist

### 1. Unit Tests
* Write unit tests in `JournalSerializationTests.cs` verifying:
  * **Enum Index Safety**: Query enum to integer values and verify `WaitingForRetry` maps exactly to `6` and `RequiresManualDecision` maps exactly to `5`.
  * **Serialization Safety**: Create a `v2` suspend or retry journal entry and assert it writes `"v2"` in the `PayloadVersion` column.
  * **Backward Compatibility**: Deserialize legacy `v1` journal strings (which omit the version property). Assert they default gracefully to `"v1"` and parse successfully without throwing exceptions.
