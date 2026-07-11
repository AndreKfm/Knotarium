# Step A3: Database & Persistence Abstraction

## Goal
Configure persistence schemas for workflow definitions and execution states, establishing the database provider factory while separating high-frequency journal writes into a dedicated, direct ADO.NET query writer.

## Proposed Changes

### EF Core Entity Mappings
Register and map core relational schema tables in `AppDbContext.cs`:
- `WorkflowDefinition` & `WorkflowVersion` (§3 table structure).
- `ExecutionInstance` & `NodeState` (§3 table structure).
- `ExecutionJournal` (append-only timeline schema for direct ADO.NET query access) (§3, §4).
- `NodePackage` & `NodePackageVersion` (§3 table structure).
- `Credential` (credential mappings and encrypted secret properties) (§3, §11).
- `AuditEntry` (tamper-evident audit chain database model) (§3, §13).

### Scoped Provider Factory Configuration
Define the `IDatabaseProvider` assembly resolver allowing runtime selection of `SqliteDatabaseProvider` (default) or `PostgresDatabaseProvider` via `appsettings.json` (§3).

### Direct ADO.NET Journal Writer
Explicitly state that **high-speed execution journal writes live outside of `AppDbContext`**.
- Implement `IExecutionJournalWriter` utilizing direct ADO.NET queries (`Microsoft.Data.Sqlite` or `Npgsql` depending on provider settings).
- Expose the writer as a singleton process to bypass EF Core change-tracking overhead on high-frequency execution runs (§3, §4).

---

## Constraints from Architecture
- **Isolation**: High-frequency journal appends must bypass EF Core change-tracking entirely, using direct ADO.NET to optimize performance and prevent process latency (§3).
- **Versioning**: Workflows must explicitly pin to specific `NodePackageVersionId` records so a workflow's pinned definition is reproducible — a newer package never silently changes a saved workflow's behaviour. (Full deterministic replay additionally requires pinning runtime/dependency versions and is out of scope here.) (§5).
- **Credential Storage**: Credentials must be stored encrypted at rest using keys derived outside the database instance (§11).
