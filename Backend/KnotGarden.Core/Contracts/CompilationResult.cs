using KnotGarden.Core.Domain;
using System.Collections.Immutable;
using System.Linq;

namespace KnotGarden.Core.Contracts;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record CompilationDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    NodeId? NodeId = null,
    string? EdgeId = null);

public sealed record CompilationResult(
    ExecutionPlan? Plan,
    ImmutableArray<CompilationDiagnostic> Diagnostics)
{
    public bool IsSuccess => Plan != null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
