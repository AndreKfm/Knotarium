// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Knotarium.NodeRuntime;

/// <summary>
/// Severity of an analyzer finding. Kept independent from Roslyn's
/// <see cref="DiagnosticSeverity"/> so the analyzer's public surface does not leak
/// the compiler abstraction.
/// </summary>
public enum AnalysisSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>Stable diagnostic identifiers. These strings are part of the API contract
/// (consumed by the sandbox service, the UI and tests) and must not change value.</summary>
public static class BannedApiDiagnosticCodes
{
    public const string BannedApi = "BANNED_API";
    public const string StaticMutableState = "STATIC_MUTABLE_STATE";
    public const string SyntaxError = "SYNTAX_ERROR";
    public const string SourceTooLarge = "SOURCE_TOO_LARGE";
    public const string AnalyzerError = "ANALYZER_ERROR";
}

public sealed record DiagnosticResult(
    string Code,
    string Message,
    AnalysisSeverity Severity,
    string? NodeId = null,
    int StartLine = 0,
    int StartColumn = 0,
    int EndLine = 0,
    int EndColumn = 0
);

/// <summary>
/// Static-analysis gate for user-authored node executor source.
/// <para>
/// This is a <b>best-effort early check</b>, not a security boundary: it rejects the most
/// obvious uses of dangerous APIs and any static mutable state, but it cannot constrain CPU,
/// memory or the many indirect ways managed code can reach the host. Untrusted code must still
/// be executed behind real OS-level isolation (separate process/container with resource limits).
/// </para>
/// <para>
/// Detection is <b>semantic</b>: the source is parsed into a real Roslyn compilation with the
/// full platform reference set and banned APIs are matched against the <i>resolved</i> symbol's
/// namespace, so type aliases (<c>using S = System;</c>) and casing tricks cannot bypass it.
/// A syntactic prefix check runs only as a fallback when a symbol cannot be resolved, so the
/// gate never fails open.
/// </para>
/// </summary>
public static class BannedApiAnalyzer
{
    private static readonly string[] BannedNamespaces =
    [
        "System.IO",
        "System.Diagnostics",
        "System.Reflection.Emit",
        "System.Net.Sockets",
        "System.Runtime.InteropServices",
        "System.Runtime.Loader",
        "Microsoft.Win32"
    ];

    /// <summary>Reference set used purely to resolve symbols. It intentionally exposes the full
    /// BCL so that dangerous APIs resolve to real symbols and can be recognised — resolvability
    /// here is not the same as being permitted to run.</summary>
    private static readonly Lazy<MetadataReference[]> ReferenceCache = new(BuildReferences);

    private const int MaxSourceLength = 500_000;
    private const int MaxDiagnostics = 200;

    public static IReadOnlyList<DiagnosticResult> Analyze(
        string sourceCode,
        string? nodeId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return Array.Empty<DiagnosticResult>();
        }

        if (sourceCode.Length > MaxSourceLength)
        {
            return
            [
                new DiagnosticResult(
                    BannedApiDiagnosticCodes.SourceTooLarge,
                    $"Source exceeds the {MaxSourceLength:N0}-character analysis limit and was rejected.",
                    AnalysisSeverity.Error,
                    nodeId)
            ];
        }

        try
        {
            var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
            var root = tree.GetRoot(cancellationToken);

            var results = new List<DiagnosticResult>();

            // Syntax diagnostics: ParseText does not throw on malformed C#; the errors live on
            // the tree. Surface them (best-effort analysis still continues on the partial tree).
            foreach (var syntaxError in tree.GetDiagnostics(cancellationToken)
                         .Where(d => d.Severity == DiagnosticSeverity.Error)
                         .Take(MaxDiagnostics))
            {
                results.Add(ToResult(BannedApiDiagnosticCodes.SyntaxError, syntaxError.GetMessage(),
                    AnalysisSeverity.Error, nodeId, syntaxError.Location.GetLineSpan()));
            }

            var compilation = CSharpCompilation.Create(
                "BannedApiAnalysis",
                [tree],
                ReferenceCache.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);

            var walker = new BannedApiWalker(BannedNamespaces, model, cancellationToken);
            walker.Visit(root);

            results.AddRange(walker.BuildDiagnostics(nodeId).Take(MaxDiagnostics));
            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Do not leak internal exception detail to callers.
            return
            [
                new DiagnosticResult(
                    BannedApiDiagnosticCodes.AnalyzerError,
                    "The analyzer failed to process the source. Treat the source as rejected.",
                    AnalysisSeverity.Error,
                    nodeId)
            ];
        }
    }

    private static DiagnosticResult ToResult(
        string code, string message, AnalysisSeverity severity, string? nodeId, FileLinePositionSpan span)
    {
        var start = span.StartLinePosition;
        var end = span.EndLinePosition;
        return new DiagnosticResult(code, message, severity, nodeId,
            start.Line + 1, start.Character + 1, end.Line + 1, end.Character + 1);
    }

    private static MetadataReference[] BuildReferences()
    {
        // Trusted Platform Assemblies = the full runtime the analyzer host is running on.
        // Using them here only lets the semantic model resolve symbols; it grants the analysed
        // code nothing.
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        var refs = tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

        return refs.Length > 0
            ? refs
            // Fallback for hosts without a TPA list: at least reference core + this analyzer's deps.
            : [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
    }

    private sealed class RawHit
    {
        public required string Code { get; init; }
        public required string Message { get; init; }
        public required TextSpan Span { get; init; }
        public required FileLinePositionSpan LineSpan { get; init; }

        /// <summary>For banned-API hits, the matched banned namespace; enables containment
        /// de-duplication (e.g. <c>System.IO</c> ⊂ <c>System.IO.File.ReadAllText</c>).</summary>
        public string? BannedNamespace { get; init; }
    }

    private sealed class BannedApiWalker : CSharpSyntaxWalker
    {
        private readonly string[] _bannedNamespaces;
        private readonly SemanticModel _model;
        private readonly CancellationToken _cancellationToken;
        private readonly List<RawHit> _hits = [];
        private readonly HashSet<(string, int, int)> _seen = [];
        private int _visited;

        public BannedApiWalker(string[] bannedNamespaces, SemanticModel model, CancellationToken cancellationToken)
        {
            _bannedNamespaces = bannedNamespaces;
            _model = model;
            _cancellationToken = cancellationToken;
        }

        public override void Visit(SyntaxNode? node)
        {
            if (node is null)
            {
                return;
            }

            if ((++_visited & 0x1FF) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            if (_hits.Count >= MaxDiagnostics)
            {
                return; // stop descending once the cap is reached
            }

            base.Visit(node);
        }

        // ---- Banned API detection (semantic, with syntactic fallback) --------------------------

        public override void VisitUsingDirective(UsingDirectiveSyntax node)
        {
            // `using System.IO;` — resolve the namespace symbol. `using S = System;` resolves the
            // alias target (System, not banned); its *uses* are caught at the access sites below.
            if (node.Name is not null)
            {
                if (!CheckSemantic(node.Name, node.Name)
                    && node.Alias is null)
                {
                    CheckSyntactic(node.Name.ToString(), node.Name);
                }
            }

            base.VisitUsingDirective(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // Only evaluate the outermost link of a member-access chain to avoid duplicate hits.
            if (node.Parent is not MemberAccessExpressionSyntax)
            {
                if (!CheckSemantic(node, node))
                {
                    CheckSyntactic(node.ToString(), node);
                }
            }

            base.VisitMemberAccessExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            if (!CheckSemantic(node, node.Type))
            {
                CheckSyntactic(node.Type.ToString(), node.Type);
            }

            base.VisitObjectCreationExpression(node);
        }

        public override void VisitQualifiedName(QualifiedNameSyntax node)
        {
            // Outermost qualified name only; object-creation types are handled above.
            if (node.Parent is not QualifiedNameSyntax and not ObjectCreationExpressionSyntax)
            {
                if (!CheckSemantic(node, node))
                {
                    CheckSyntactic(node.ToString(), node);
                }
            }

            base.VisitQualifiedName(node);
        }

        /// <summary>Resolves <paramref name="symbolSource"/> to a symbol and, if its namespace is
        /// banned, records a hit anchored at <paramref name="anchor"/>. Returns true when a symbol
        /// was resolved (so the caller can decide whether the syntactic fallback is needed).</summary>
        private bool CheckSemantic(SyntaxNode symbolSource, SyntaxNode anchor)
        {
            var info = _model.GetSymbolInfo(symbolSource, _cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is null)
            {
                return false;
            }

            var ns = NamespaceOf(symbol);
            if (ns is not null && TryMatchBanned(ns, out var banned))
            {
                AddBanned(anchor, banned!);
            }

            return true;
        }

        private void CheckSyntactic(string path, SyntaxNode anchor)
        {
            if (TryMatchBanned(path, out var banned))
            {
                AddBanned(anchor, banned!);
            }
        }

        // ---- Static mutable state (forbidden everywhere) ---------------------------------------

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                // `const` fields never carry the `static` keyword, so they never reach here.
                var isReadonly = node.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);

                foreach (var variable in node.Declaration.Variables)
                {
                    var type = (_model.GetDeclaredSymbol(variable, _cancellationToken) as IFieldSymbol)?.Type;
                    if (!isReadonly || !IsImmutable(type))
                    {
                        AddStaticState(variable, $"Static mutable field '{variable.Identifier.Text}' is forbidden.");
                    }
                }
            }

            base.VisitFieldDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                var accessors = node.AccessorList?.Accessors;
                var hasSetter = accessors?.Any(a =>
                    a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) ?? false;

                if (hasSetter)
                {
                    AddStaticState(node, $"Static mutable property '{node.Identifier.Text}' is forbidden.");
                }
                else
                {
                    // A get-only *auto*-property has a hidden mutable backing field; if its type is
                    // mutable it is shared state. A computed get-only property (=> expr / get{...})
                    // holds no backing state and is allowed.
                    var isAutoProperty = node.ExpressionBody is null &&
                        (accessors?.All(a => a.Body is null && a.ExpressionBody is null) ?? false);
                    if (isAutoProperty)
                    {
                        var type = (_model.GetDeclaredSymbol(node, _cancellationToken) as IPropertySymbol)?.Type;
                        if (!IsImmutable(type))
                        {
                            AddStaticState(node, $"Static mutable property '{node.Identifier.Text}' is forbidden.");
                        }
                    }
                }
            }

            base.VisitPropertyDeclaration(node);
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    AddStaticState(variable, $"Static event '{variable.Identifier.Text}' is forbidden.");
                }
            }

            base.VisitEventFieldDeclaration(node);
        }

        public override void VisitEventDeclaration(EventDeclarationSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                AddStaticState(node, $"Static event '{node.Identifier.Text}' is forbidden.");
            }

            base.VisitEventDeclaration(node);
        }

        // ---- Hit collection & post-processing --------------------------------------------------

        private void AddBanned(SyntaxNode anchor, string bannedNamespace)
        {
            var span = anchor.Span;
            if (!_seen.Add((BannedApiDiagnosticCodes.BannedApi + bannedNamespace, span.Start, span.End)))
            {
                return;
            }

            _hits.Add(new RawHit
            {
                Code = BannedApiDiagnosticCodes.BannedApi,
                Message = $"Access to banned namespace '{bannedNamespace}' is forbidden.",
                Span = span,
                LineSpan = anchor.GetLocation().GetLineSpan(),
                BannedNamespace = bannedNamespace
            });
        }

        private void AddStaticState(SyntaxNode anchor, string message)
        {
            var span = anchor.Span;
            if (!_seen.Add((BannedApiDiagnosticCodes.StaticMutableState, span.Start, span.End)))
            {
                return;
            }

            _hits.Add(new RawHit
            {
                Code = BannedApiDiagnosticCodes.StaticMutableState,
                Message = message,
                Span = span,
                LineSpan = anchor.GetLocation().GetLineSpan()
            });
        }

        public IEnumerable<DiagnosticResult> BuildDiagnostics(string? nodeId)
        {
            var banned = _hits.Where(h => h.Code == BannedApiDiagnosticCodes.BannedApi).ToList();

            foreach (var hit in _hits)
            {
                // Collapse nested banned-API hits for the same namespace: keep only the outermost
                // access (e.g. drop `System.IO` when `System.IO.File.ReadAllText` is also present).
                if (hit.Code == BannedApiDiagnosticCodes.BannedApi &&
                    banned.Any(o => !ReferenceEquals(o, hit) &&
                                    o.BannedNamespace == hit.BannedNamespace &&
                                    o.Span.Contains(hit.Span) &&
                                    o.Span.Length > hit.Span.Length))
                {
                    continue;
                }

                var start = hit.LineSpan.StartLinePosition;
                var end = hit.LineSpan.EndLinePosition;
                yield return new DiagnosticResult(
                    hit.Code, hit.Message, AnalysisSeverity.Error, nodeId,
                    start.Line + 1, start.Character + 1, end.Line + 1, end.Character + 1);
            }
        }

        private bool TryMatchBanned(string path, out string? banned)
        {
            foreach (var candidate in _bannedNamespaces)
            {
                if (path.Equals(candidate, StringComparison.Ordinal) ||
                    path.StartsWith(candidate + ".", StringComparison.Ordinal))
                {
                    banned = candidate;
                    return true;
                }
            }

            banned = null;
            return false;
        }

        private static string? NamespaceOf(ISymbol symbol)
        {
            INamespaceSymbol? ns = symbol switch
            {
                INamespaceSymbol n => n,
                ITypeSymbol t => t.ContainingNamespace,
                _ => symbol.ContainingType?.ContainingNamespace ?? symbol.ContainingNamespace
            };

            return ns is null || ns.IsGlobalNamespace ? null : ns.ToDisplayString();
        }

        private static bool IsImmutable(ITypeSymbol? type)
        {
            if (type is null)
            {
                return false; // unresolved → treat conservatively as mutable
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_Char:
                case SpecialType.System_String:
                case SpecialType.System_DateTime:
                    return true;
            }

            return type.ToDisplayString() switch
            {
                "System.Guid" => true,
                "System.TimeSpan" => true,
                "System.DateTimeOffset" => true,
                "System.Version" => true,
                "System.Uri" => true,
                _ => false
            };
        }
    }
}
