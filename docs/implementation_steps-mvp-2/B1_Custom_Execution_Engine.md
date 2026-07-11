# Step B1: Custom Hosted Execution Engine

## Goal
Implement a single-worker hosted execution engine traversing compiled DAGs, managing sequential step tracking, implementing cooperative cancellation policies, and serializing writes through a guarded journal writer.

## Proposed Changes

### Single-Worker Execution Loop
Implement a hosted `BackgroundService` execution thread:
- Serializes all state changes through a single shared `IExecutionJournalWriter` (§4).
- Add a **startup guard** validating at runtime that exactly one execution worker process is active to prevent multi-instance write collisions (§4).

### Cooperative Cancellation & Hard-Timeout
Refactor the node thread execution tracking to enforce cooperative cancellation policies:
1. Pass a cooperative `CancellationToken` to `INodeExecutor.ExecuteAsync`. The executing node is contractually responsible for regularly checking this token (§4).
2. Start a cooperative timeout countdown (default 5s, capped at 60s configurable per node).
3. If the timeout expires before cooperative exit, **stop awaiting execution progress**, record a **hard-timeout** event inside the Execution Journal, and exit the execution runner thread. No unsafe `Thread.Abort` operations are used (§4).

### Performance Optimization
- Optimize execution performance by skipping EF Core change tracking on the hot-path journal table writes.
- Write append-only journal structures directly through ADO.NET, ensuring minimum latency (§3).

---

## Constraints from Architecture
- **Worker Isolation**: The startup guard must strictly enforce exactly one running executor worker per database instance to preserve execution timeline integrity (§4).
- **Graceful Cancellation**: Nodes must observe cooperative cancellation tokens directly; hard-timeouts must only terminate engine wait scopes, leaving thread cleanup safely to .NET Core (no forced thread kills) (§4).
- **Journal Integrity**: Every state change must write a corresponding journal entry to maintain the journal as the absolute source of truth (§4).
