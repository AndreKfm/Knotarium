# Step B4: Dogfooded Built-in Node Packages

## Goal
Extract and bundle the 12 core workflow nodes as self-contained package directories under `./nodes/`, fully dogfooding the extensibility manifest architecture.

## Proposed Changes

### Built-in Packages Directory Schema
Move all 12 core nodes under the `./nodes/` folder:
- **`nodes/Start`**: Declarative trigger node manifest (§10).
- **`nodes/ManualTrigger`**: Declarative manual run trigger manifest (§10).
- **`nodes/WebhookTrigger`**: Compiled C# webhook trigger class (§10).
- **`nodes/Condition`**: Declarative boolean mapper manifest (§10).
- **`nodes/Switch`**: Declarative branch switch mapping manifest (§10).
- **`nodes/SetVariable`**: Declarative state variable configuration manifest (§10).
- **`nodes/Transform`**: Declarative JSONPath transform manifest (§10).
- **`nodes/Merge`**: Compiled data array merger (§10).
- **`nodes/HttpRequest`**: Compiled REST HTTP node using `"http"` and `"credentials"` capabilities (§10).
- **`nodes/Delay`**: Compiled timing delay node (§10).
- **`nodes/Log`**: Declarative standard output logger (§10).
- **`nodes/End`**: Declarative boundary execution terminator (§10).

Each package must house its corresponding `manifest.yaml` and, where applicable, its signed `Executor.cs` class. Furthermore, packages may optionally include an `icon.svg` for frontend palette rendering, and a `tests/cases.yaml` housing declarative input/output test assertions for verification runs (§5).

---

## Constraints from Architecture
- **Package Dogfooding**: Built-in nodes must not utilize any private APIs; they must be structured exactly like custom extension packages, compiling and running strictly through the standard capability-based `INodeContext` boundaries (§5, §10).
- **Version Immutability**: Manifest schemas must declare precise major/minor versions to allow workflows to pin execution behaviors safely (§5).
- **Capability Compliance**: Built-in nodes must strictly declare all ambient capabilities required (e.g. `HttpRequest` must explicitly state its `http` dependency in the manifest) (§5).
