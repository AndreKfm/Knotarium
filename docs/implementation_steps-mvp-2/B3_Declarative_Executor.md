# Step B3: Declarative Node Runtime & Expression Evaluator

## Goal
Implement the core Tier 1 interpreter (`DeclarativeExecutor`) along with a secure, handwritten expression evaluator DSL to resolve dynamic cross-node references.

## Proposed Changes

### Dynamic Declarative Node Interpreter
Implement the `DeclarativeExecutor` to dynamically parse declarative node manifests, matching input parameters and evaluating transformations inside the host engine thread (§5).

### Handwritten Expression Evaluator DSL
Design and build a secure, lightweight, handwritten expression evaluator:
- **Scope**: Parses expression tokens enclosed within `{{ ... }}` placeholders.
- **Identifier Paths**: Resolves cross-node output references such as `{{ $node.X.output.Y }}` (§8).
- **Operators**: Hand-evaluate a strictly fixed set of logical and math operators: `==`, `!=`, `&&`, `||`, `+`, `-`, `*`, `/` (§8).
- **Function Allowlist**: Strictly evaluate only the fixed function set: `now()`, `uuid()`, `coalesce()`, `length()` (§8).
- **Transformations**: Support reading nested JSON structures via JSONPath path segments (§8).

---

## Constraints from Architecture
- **Sandbox Boundary**: The expression engine must be handwritten and execute in-process with a strictly closed allowlist. No reflection, static singletons, or dynamic javascript interpreters (Jint) are permitted (§8, DR-001).
- **Secret Access**: Expression evaluations must never substitute secret values into logged or journaled diagnostic strings; secrets are resolved late inside capability accessors (§8, §11).
- **Isolation Invariant**: Variable lookup scopes are limited to prior nodes in the same execution instance (§8).
