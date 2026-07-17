// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using Knotarium.NodeRuntime;

namespace Knotarium.NodeRuntime.Tests;

public class BannedApiAnalyzerTests
{
    private static string BuildExecutorCode(string body, string helperCode = "")
    {
        return $@"
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Knotarium.Core.Contracts;
            using Knotarium.Core.Domain;

            public class MyCustomExecutor : INodeExecutor
            {{
                public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext context, CancellationToken cancellationToken)
                {{
                    {body}
                    return new ValueTask<NodeResult>(new NodeResult(""out"", null, NodeExecutionStatus.Succeeded));
                }}
            }}

            {helperCode}
        ";
    }

    [Fact]
    public void Analyze_WithSafeCode_ReturnsNoDiagnostics()
    {
        // Arrange
        var code = BuildExecutorCode(@"
            var x = 10;
            var y = 20;
            var sum = x + y;
            context.Logger.LogInformation(""Sum: "" + sum);
        ");

        // Act
        var diags = BannedApiAnalyzer.Analyze(code);

        // Assert
        Assert.Empty(diags);
    }

    [Theory]
    [InlineData("using System.IO;")]
    [InlineData("using System.Diagnostics;")]
    [InlineData("using System.Reflection.Emit;")]
    [InlineData("using System.Net.Sockets;")]
    public void Analyze_WithBannedUsing_ReturnsBannedApiDiagnostic(string bannedUsing)
    {
        // Arrange
        var code = $@"
            {bannedUsing}
            using System;
            using Knotarium.Core.Contracts;

            public class SafeExecutor : INodeExecutor
            {{
                public System.Threading.Tasks.ValueTask<Knotarium.Core.Contracts.NodeResult> ExecuteAsync(
                    Knotarium.Core.Contracts.NodeInput input, 
                    Knotarium.Core.Contracts.INodeContext context, 
                    System.Threading.CancellationToken cancellationToken)
                {{
                    return default;
                }}
            }}
        ";

        // Act
        var diags = BannedApiAnalyzer.Analyze(code);

        // Assert
        Assert.NotEmpty(diags);
        Assert.Contains(diags, d => d.Code == "BANNED_API");
        var diag = diags.First(d => d.Code == "BANNED_API");
        Assert.Contains("forbidden", diag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText(\"test.txt\");")]
    [InlineData("System.Diagnostics.Process.Start(\"cmd.exe\");")]
    [InlineData("var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);")]
    public void Analyze_WithBannedMemberAccess_ReturnsBannedApiDiagnostic(string bannedCode)
    {
        // Arrange
        var code = BuildExecutorCode(bannedCode);

        // Act
        var diags = BannedApiAnalyzer.Analyze(code);

        // Assert
        Assert.NotEmpty(diags);
        Assert.Contains(diags, d => d.Code == "BANNED_API");
    }

    [Fact]
    public void Analyze_WithStaticMutableStateOutsideExecutor_ReturnsStaticMutableStateDiagnostic()
    {
        // Arrange
        var helperCode = @"
            public static class Helper
            {
                public static int MutableCounter = 0;
            }
        ";
        var code = BuildExecutorCode("Helper.MutableCounter++;", helperCode);

        // Act
        var diags = BannedApiAnalyzer.Analyze(code);

        // Assert
        var diag = Assert.Single(diags);
        Assert.Equal("STATIC_MUTABLE_STATE", diag.Code);
        Assert.Contains("forbidden", diag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_WithStaticPropertyMutableStateOutsideExecutor_ReturnsStaticMutableStateDiagnostic()
    {
        // Arrange
        var helperCode = @"
            public class Helper
            {
                public static string MutableProp { get; set; } = """";
            }
        ";
        var code = BuildExecutorCode("Helper.MutableProp = \"foo\";", helperCode);

        // Act
        var diags = BannedApiAnalyzer.Analyze(code);

        // Assert
        var diag = Assert.Single(diags);
        Assert.Equal("STATIC_MUTABLE_STATE", diag.Code);
        Assert.Contains("forbidden", diag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_WithStaticReadonlyOrConstOutsideExecutor_IsAllowed()
    {
        // Arrange
        var helperCode = @"
            public static class SafeConstants
            {
                public const string AppName = ""Knotarium"";
                public static readonly int MaxRetries = 3;
            }
        ";
        var code = BuildExecutorCode("var retries = SafeConstants.MaxRetries;", helperCode);

        // Act
        var diags = BannedApiAnalyzer.Analyze(code);

        // Assert
        Assert.Empty(diags);
    }

    // ---- Bypass / hardening regression tests (semantic analysis) ---------------------------

    [Fact]
    public void Analyze_WithAliasedBannedNamespace_IsDetected()
    {
        // `using S = System;` then S.IO.File.* is syntactically not "System.IO" but resolves to it.
        var code = @"
            using S = System;
            using Knotarium.Core.Contracts;

            public class AliasExecutor : INodeExecutor
            {
                public System.Threading.Tasks.ValueTask<NodeResult> ExecuteAsync(
                    NodeInput input, INodeContext context, System.Threading.CancellationToken cancellationToken)
                {
                    var text = S.IO.File.ReadAllText(""secret.txt"");
                    return default;
                }
            }
        ";

        var diags = BannedApiAnalyzer.Analyze(code);

        Assert.Contains(diags, d => d.Code == "BANNED_API");
    }

    [Fact]
    public void Analyze_WithNewlyBannedNamespaces_IsDetected()
    {
        foreach (var access in new[]
                 {
                     "System.Runtime.InteropServices.NativeLibrary.Load(\"x\");",
                     "System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(null);",
                     "Microsoft.Win32.Registry.LocalMachine.OpenSubKey(\"x\");"
                 })
        {
            var diags = BannedApiAnalyzer.Analyze(BuildExecutorCode(access));
            Assert.Contains(diags, d => d.Code == "BANNED_API");
        }
    }

    [Fact]
    public void Analyze_WithStaticMutableStateInsideExecutor_IsForbidden()
    {
        // Static mutable state is forbidden EVERYWHERE, including inside the executor itself.
        var code = @"
            using System.Collections.Generic;
            using Knotarium.Core.Contracts;

            public class StatefulExecutor : INodeExecutor
            {
                public static Dictionary<string, object> GlobalState = new();

                public System.Threading.Tasks.ValueTask<NodeResult> ExecuteAsync(
                    NodeInput input, INodeContext context, System.Threading.CancellationToken cancellationToken)
                {
                    return default;
                }
            }
        ";

        var diags = BannedApiAnalyzer.Analyze(code);

        Assert.Contains(diags, d => d.Code == "STATIC_MUTABLE_STATE");
    }

    [Fact]
    public void Analyze_WithStaticReadonlyMutableCollection_IsForbidden()
    {
        // `readonly` prevents reassignment, not mutation of the referenced object.
        var helperCode = @"
            using System.Collections.Generic;
            public static class Cache
            {
                public static readonly Dictionary<string, object> State = new();
            }
        ";
        var code = BuildExecutorCode("Cache.State[\"k\"] = 1;", helperCode);

        var diags = BannedApiAnalyzer.Analyze(code);

        Assert.Contains(diags, d => d.Code == "STATIC_MUTABLE_STATE");
    }

    [Fact]
    public void Analyze_WithGetOnlyStaticMutableProperty_IsForbidden()
    {
        var helperCode = @"
            using System.Collections.Generic;
            public static class Store
            {
                public static List<string> Values { get; } = new();
            }
        ";
        var code = BuildExecutorCode("Store.Values.Add(\"x\");", helperCode);

        var diags = BannedApiAnalyzer.Analyze(code);

        Assert.Contains(diags, d => d.Code == "STATIC_MUTABLE_STATE");
    }

    [Fact]
    public void Analyze_WithStaticEvent_IsForbidden()
    {
        var helperCode = @"
            using System;
            public static class Bus
            {
                public static event EventHandler? Changed;
            }
        ";
        var code = BuildExecutorCode("var _ = 1;", helperCode);

        var diags = BannedApiAnalyzer.Analyze(code);

        Assert.Contains(diags, d => d.Code == "STATIC_MUTABLE_STATE");
    }

    [Fact]
    public void Analyze_WithHelperNamedExecuteAsync_DoesNotWhitelistStaticState()
    {
        // A random class exposing an ExecuteAsync method used to be treated as the executor and
        // thereby allowed to hold static state. It must not be.
        var helperCode = @"
            public class RandomHelper
            {
                public static int Counter = 0;
                public void ExecuteAsync() { }
            }
        ";
        var code = BuildExecutorCode("RandomHelper.Counter++;", helperCode);

        var diags = BannedApiAnalyzer.Analyze(code);

        Assert.Contains(diags, d => d.Code == "STATIC_MUTABLE_STATE");
    }

    [Fact]
    public void Analyze_ReportsSourcePosition()
    {
        var code = BuildExecutorCode("System.IO.File.ReadAllText(\"x\");");

        var diag = BannedApiAnalyzer.Analyze(code).First(d => d.Code == "BANNED_API");

        Assert.True(diag.StartLine > 0);
        Assert.True(diag.StartColumn > 0);
    }

    [Fact]
    public void Analyze_DeduplicatesNestedBannedAccess()
    {
        var code = BuildExecutorCode("System.IO.File.ReadAllText(\"x\");");

        var banned = BannedApiAnalyzer.Analyze(code).Where(d => d.Code == "BANNED_API").ToList();

        Assert.Single(banned);
    }

    [Fact]
    public void Analyze_WithOverlongSource_IsRejected()
    {
        var huge = new string('x', 600_000);

        var diags = BannedApiAnalyzer.Analyze(huge);

        Assert.Contains(diags, d => d.Code == "SOURCE_TOO_LARGE");
    }

    [Property(MaxTest = 20)]
    public bool FsCheck_Banned_Api_Static_Analysis_Coverage(NonNull<string> randomString)
    {
        var input = randomString.Item;

        // Skip input strings that might cause direct compilation/lexer crashes or syntax errors
        if (input.Contains("\"") || input.Contains("\\") || input.Contains("\n") || input.Contains("\r"))
        {
            return true;
        }

        // Test Scenario 1: Clean code containing random variable assignment
        var cleanCode = BuildExecutorCode($"string myVar = \"{input}\";");
        var cleanDiags = BannedApiAnalyzer.Analyze(cleanCode);

        // Check if cleanDiags is empty. If it contains a BANNED_API, it should be because the random string itself accidentally contains a banned token (like "System.IO").
        var cleanPassed = true;
        if (cleanDiags.Any(d => d.Code == "BANNED_API"))
        {
            cleanPassed = input.Contains("System.IO") || input.Contains("System.Diagnostics") || input.Contains("System.Net.Sockets");
        }

        // Test Scenario 2: Force inject a banned API usage
        var badCode = BuildExecutorCode($"string myVar = \"{input}\"; System.IO.Directory.CreateDirectory(\"test\");");
        var badDiags = BannedApiAnalyzer.Analyze(badCode);
        var badFlagged = badDiags.Any(d => d.Code == "BANNED_API");

        return cleanPassed && badFlagged;
    }
}
