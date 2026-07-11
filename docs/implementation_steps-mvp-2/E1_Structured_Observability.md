# Step E1: Structured Observability

## Goal
Implement structured logging pipelines and establish the full OpenTelemetry monitoring, metrics, and tracing instrumentation.

## Proposed Changes

### Structured JSON Logging
Configure Serilog to format outputs in structured JSON:
- Enrich every statement with contextual properties (`ExecutionInstanceId`, `NodeId`, etc.) (§12).
- Mask sensitive arguments via a global logging filter to protect secrets (§11, §12).

### OpenTelemetry Metric Instruments
Configure OpenTelemetry metrics to expose key operational indicators (§12):
- **Counters**:
  - `executions_started_total`
  - `executions_completed_total`
  - `executions_failed_total`
  - `journal_writes_total`
- **Histograms**:
  - `node_execution_duration_seconds{node_type}`
  - `journal_write_latency_seconds`
- **Gauges**:
  - `running_executions`
  - `loaded_node_packages`

### OpenTelemetry Tracing
Build span trace scopes:
- Root span captures the complete `ExecutionInstance` lifecycle (§12).
- Nested child spans capture individual node executions and outbound capability calls (§12).

---

## Constraints from Architecture
- **PII Protection**: Telemetry logs and trace arguments must filter all PII and secret tokens, preserving execution masking boundaries (§11, §12).
- **Latency Invariant**: Metric gathering must run on high-performance memory buffers, introducing zero latency into the direct journal writes (§12).
- **Trace Context**: Span contexts must be propagated dynamically through downstream capability operations (§12).
