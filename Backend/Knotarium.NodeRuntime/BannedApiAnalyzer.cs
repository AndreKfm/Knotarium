// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Knotarium.NodeRuntime;

public sealed record DiagnosticResult(
    string Code,
    string Message,
    string Severity,
    string? NodeId = null
);

public static class BannedApiAnalyzer
{
    private static readonly string[] BannedNamespaces = new[]
    {
        "System.IO",
        "System.Diagnostics",
        "System.Reflection.Emit",
        "System.Net.Sockets"
    };

    public static List<DiagnosticResult> Analyze(string sourceCode, string? nodeId = null)
    {
        var diagnostics = new List<DiagnosticResult>();
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return diagnostics;
        }

        try
        {
            var tree = CSharpSyntaxTree.ParseText(sourceCode);
            var root = tree.GetRoot();
            var walker = new BannedApiWalker(BannedNamespaces, nodeId);
            walker.Visit(root);
            diagnostics.AddRange(walker.Diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticResult(
                "COMPILATION_ERROR",
                $"Failed to parse source tree: {ex.Message}",
                "Error",
                nodeId
            ));
        }

        return diagnostics;
    }

    private class BannedApiWalker : CSharpSyntaxWalker
    {
        private readonly string[] _bannedNamespaces;
        private readonly string? _nodeId;
        private string? _executorClassName;

        public List<DiagnosticResult> Diagnostics { get; } = new();

        public BannedApiWalker(string[] bannedNamespaces, string? nodeId)
        {
            _bannedNamespaces = bannedNamespaces;
            _nodeId = nodeId;
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            // Identify if this is the executor class
            var implementsNodeExecutor = false;
            if (node.BaseList != null)
            {
                implementsNodeExecutor = node.BaseList.Types
                    .Any(t => t.ToString().Contains("INodeExecutor"));
            }

            var hasExecuteAsync = node.Members
                .OfType<MethodDeclarationSyntax>()
                .Any(m => m.Identifier.Text == "ExecuteAsync");

            if (implementsNodeExecutor || hasExecuteAsync)
            {
                // Record the name of the main executor class
                _executorClassName = node.Identifier.Text;
            }

            base.VisitClassDeclaration(node);
        }

        public override void VisitUsingDirective(UsingDirectiveSyntax node)
        {
            if (node.Name != null)
            {
                var ns = node.Name.ToString();
                if (IsBannedNamespace(ns))
                {
                    Diagnostics.Add(new DiagnosticResult(
                        "BANNED_API",
                        $"Using namespace '{ns}' is forbidden.",
                        "Error",
                        _nodeId
                    ));
                }
            }
            base.VisitUsingDirective(node);
        }

        public override void VisitQualifiedName(QualifiedNameSyntax node)
        {
            var name = node.ToString();
            if (IsBannedNamespace(name))
            {
                Diagnostics.Add(new DiagnosticResult(
                    "BANNED_API",
                    $"Accessing member under namespace '{name}' is forbidden.",
                    "Error",
                    _nodeId
                ));
            }
            base.VisitQualifiedName(node);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            // Check if this identifier is part of a member access or object creation that starts with a banned namespace
            var fullPath = GetFullAccessPath(node);
            if (IsBannedNamespace(fullPath))
            {
                Diagnostics.Add(new DiagnosticResult(
                    "BANNED_API",
                    $"Accessing member '{fullPath}' is forbidden.",
                    "Error",
                    _nodeId
                ));
            }
            base.VisitIdentifierName(node);
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            // Check if field is static
            var isStatic = node.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            if (isStatic)
            {
                // Check if it's mutable: does not have readonly or const modifiers
                var isReadonly = node.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
                var isConst = node.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword));

                if (!isReadonly && !isConst)
                {
                    // Check parent class
                    var parentClass = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                    var isInsideExecutor = parentClass != null && parentClass.Identifier.Text == _executorClassName;

                    if (!isInsideExecutor)
                    {
                        var fieldNames = string.Join(", ", node.Declaration.Variables.Select(v => v.Identifier.Text));
                        Diagnostics.Add(new DiagnosticResult(
                            "STATIC_MUTABLE_STATE",
                            $"Static mutable field(s) '{fieldNames}' outside the executor class '{_executorClassName}' is forbidden.",
                            "Error",
                            _nodeId
                        ));
                    }
                }
            }
            base.VisitFieldDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            // Check if property is static
            var isStatic = node.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            if (isStatic)
            {
                // Check if it is mutable (has set accessor)
                var hasSetAccessor = false;
                if (node.AccessorList != null)
                {
                    hasSetAccessor = node.AccessorList.Accessors
                        .Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));
                }

                if (hasSetAccessor)
                {
                    // Check parent class
                    var parentClass = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                    var isInsideExecutor = parentClass != null && parentClass.Identifier.Text == _executorClassName;

                    if (!isInsideExecutor)
                    {
                        Diagnostics.Add(new DiagnosticResult(
                            "STATIC_MUTABLE_STATE",
                            $"Static mutable property '{node.Identifier.Text}' outside the executor class '{_executorClassName}' is forbidden.",
                            "Error",
                            _nodeId
                        ));
                    }
                }
            }
            base.VisitPropertyDeclaration(node);
        }

        private bool IsBannedNamespace(string path)
        {
            return _bannedNamespaces.Any(banned =>
                path.Equals(banned, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(banned + ".", StringComparison.OrdinalIgnoreCase));
        }

        private string GetFullAccessPath(IdentifierNameSyntax node)
        {
            // Walk up to find the fully qualified parent or member access expression
            SyntaxNode current = node;
            while (current.Parent is QualifiedNameSyntax or MemberAccessExpressionSyntax)
            {
                current = current.Parent;
            }
            return current.ToString();
        }
    }
}
