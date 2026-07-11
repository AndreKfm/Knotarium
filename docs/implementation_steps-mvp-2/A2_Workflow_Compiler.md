# Step A2: Workflow Compiler Upgrades

## Goal
Enhance the visual workflow compiler to validate graphs statically, performing schema validations, cycle detection, and edge completeness checks alongside compiler-time subflow inlining.

## Proposed Changes

### Static Graph Validation
Integrate rigorous graph validation rules inside `WorkflowCompiler.cs`:
1. **Schema Validation**: Validate each node's properties structure against its corresponding package `manifest.yaml` requirements.
2. **Required Parameters**: Verify that all parameters flagged as `required: true` in the manifest are supplied either as direct values or expression bindings.
3. **Cycle Detection**: Walk the node graph using Depth-First Search (DFS) or Kahn's algorithm, throwing compilation failures on cyclic references.
4. **Edge Integrity**: Validate edge integrity, checking for missing nodes, dangling edges, or invalid socket mappings.

### Subflow Inlining
Update the DAG compiler to recursively inline nested subflow nodes during the creation of the compiled `ExecutionPlan`:
- Merge subflow nodes and rename variable scope identifiers with a unique subflow instance ID prefix to prevent namespace collisions (§7).
- Re-bind edges entering or exiting the subflow node to target their designated subflow boundaries in the compiled layout.

---

## Constraints from Architecture
- **Invariants**: Compiled DAG structures must inline variables statically to preserve execution-time namespace isolation boundaries (§7).
- **Validation**: Cycle detection and required parameter checks are non-overridable, preventing run-time DAG traversal deadlocks (§4).
- **Diagnostics**: Edge validations must output standard `CompilationDiagnostic` records featuring unique codes and target `NodeId` references (§7).
