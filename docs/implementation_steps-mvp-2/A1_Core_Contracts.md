# Step A1: Core Contracts & ID Types

## Goal
Implement the foundational domain structures, typed enums, strongly-typed ID records, INodeExecutor/INodeContext contract interfaces, and the `INodePackageGenerator` placeholder.

## Proposed Changes

### Strongly-Typed Identifiers (§7)
Create C# strongly-typed record structures for core model IDs:
```csharp
public readonly record struct NodeId(string Value);
public readonly record struct WorkflowDefinitionId(string Value);
public readonly record struct WorkflowVersionId(Guid Value);
public readonly record struct ExecutionInstanceId(Guid Value);
public readonly record struct NodePackageId(string Value);
public readonly record struct NodePackageVersionId(Guid Value);
```

### Typed Enums (§7)
Define typed enums for node behavior and side-effect categories:
```csharp
public enum NodeTier { Declarative, Compiled }

public enum NodeSideEffectKind
{
    IdempotentSideEffect,
    NonIdempotentSideEffect
}

public enum RecoveryMode
{
    RetryAutomatically,
    FailImmediately
}

public enum NodeExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled
}
```

### Node Manifest Domain Schemas (§5, §7)
Define the primary manifest parser contracts:
```csharp
public sealed record NodePackageManifest(
    NodePackageId Id,
    string Version,
    string DisplayName,
    string Category,
    NodeTier Tier,
    NodeSideEffectKind SideEffectKind,
    RecoveryMode RecoveryMode,
    int DefaultTimeoutSeconds,
    List<string> Capabilities,
    List<ParameterDefinition> Parameters,
    List<OutputDefinition> Outputs
);

public sealed record ParameterDefinition(
    string Name,
    string Type,
    bool Required,
    bool Expression
);

public sealed record OutputDefinition(
    string Name
);
```

### Execution Contracts (§7)
Implement the core visual node C# interfaces:
```csharp
public interface INodeExecutor
{
    ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken);
}

public sealed record NodeInput(IReadOnlyDictionary<string, JsonElement> Parameters);

public sealed record NodeResult(
    string OutputName,
    JsonElement? Payload,
    NodeExecutionStatus Status);

public interface INodeContext
{
    ILogger Logger { get; }
    IWorkflowState State { get; }
    IHttpClient? Http { get; }
    ICredentialAccessor? Credentials { get; }
}
```

### Compilation Outputs (§7)
Define compiler output contracts:
```csharp
public sealed record CompilationResult(
    ExecutionPlan Plan,
    List<CompilationDiagnostic> Diagnostics,
    bool IsSuccess);

public sealed record CompilationDiagnostic(
    string Code,
    string Message,
    NodeId? NodeId);
```

### AI Extensibility Interface Placeholder (DR-002)
Add `INodePackageGenerator` placeholder in `KnotGarden.Core`:
```csharp
public interface INodePackageGenerator
{
    Task<GeneratedPackage> GenerateAsync(GenerationRequest request, CancellationToken ct);
}
```

---

## Constraints from Architecture
- **Invariants**: Schema fields, strongly-typed IDs, and interfaces must remain strictly aligned with the contract schemas specified in §7 of the Architecture.
- **Enforcement**: Explicit enums (`NodeSideEffectKind`, `RecoveryMode`) must be utilized instead of raw strings to avoid compilation drift and runtime parsing ambiguity (§5, §7).
- **Placeholder Rule**: `INodePackageGenerator` must compile with no concrete host dependencies to protect Phase 2 MCP alignment boundaries (DR-002).
