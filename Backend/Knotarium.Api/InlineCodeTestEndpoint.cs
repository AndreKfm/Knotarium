// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;

namespace Knotarium.Api;

/// <summary>Request to run an inline script once, in isolation, for editor testing.</summary>
public sealed record InlineCodeTestRequest(
    string Code,
    string? Language,
    // Optional sample variables/inputs the script can read via Input.Get / state during the test.
    Dictionary<string, JsonElement>? Inputs
);

public sealed record InlineCodeTestResponse(
    bool Success,
    JsonElement? Output,
    string? Error,
    List<string> Logs,
    long ElapsedMs
);

public static class InlineCodeTestEndpoint
{
    // Console.Out is process-global; serialize console-capturing test runs so concurrent
    // requests don't capture each other's output.
    private static readonly SemaphoreSlim _consoleLock = new(1, 1);

    public static void MapInlineCodeTestEndpoint(this WebApplication app)
    {
        // Runs a single inline script through the SAME InlineCodeNodeTask used at workflow runtime,
        // so "Test run" behaves exactly like a real execution — minus the surrounding workflow.
        app.MapPost("/api/inline-code/test", async (
            InlineCodeTestRequest request,
            CSharpScriptCompiler compiler,
            IHttpClientFactory httpClientFactory,
            ICredentialAccessor credentialAccessor,
            ICapabilityPolicy capabilities,
            CancellationToken cancellationToken) =>
        {
            var logs = new List<string>();
            var logger = new CapturingLogger(logs);
            // The design-time test runs the SAME task, so it is gated by the same capability switch.
            var task = new InlineCodeNodeTask(httpClientFactory, credentialAccessor, logger, compiler, capabilities);

            var inputs = new Dictionary<string, object>
            {
                ["code"] = request.Code ?? string.Empty,
                ["language"] = request.Language ?? "csharp",
            };
            var globals = new Dictionary<string, object>();
            if (request.Inputs != null)
            {
                foreach (var kv in request.Inputs)
                {
                    inputs[kv.Key] = kv.Value;
                    globals[kv.Key] = kv.Value;
                }
            }

            var context = new NodeExecutionContext(
                WorkflowDefinitionId.New(),
                Guid.NewGuid(),
                new NodeId("inline-test"),
                inputs,
                globals);

            // Capture Console output during the test run so users who write Console.WriteLine
            // (instead of Logger) still see their output in the panel. Console is process-global,
            // so we serialize console-capturing test runs to avoid interleaving across requests.
            var sw = Stopwatch.StartNew();
            LegacyNodeResult result;
            string consoleText;
            await _consoleLock.WaitAsync(cancellationToken);
            var originalOut = Console.Out;
            using var captured = new StringWriter();
            try
            {
                Console.SetOut(captured);
                result = await task.ExecuteAsync(context, cancellationToken);
            }
            finally
            {
                Console.SetOut(originalOut);
                _consoleLock.Release();
                sw.Stop();
                consoleText = captured.ToString();
            }

            if (!string.IsNullOrEmpty(consoleText))
            {
                foreach (var line in consoleText.Replace("\r\n", "\n").Split('\n'))
                {
                    if (line.Length > 0) logs.Add($"[stdout] {line}");
                }
            }

            return result switch
            {
                LegacyNodeResult.Success s => Results.Ok(new InlineCodeTestResponse(
                    true, SerializeOutputs(s.Outputs), null, logs, sw.ElapsedMilliseconds)),
                LegacyNodeResult.Failure f => Results.Ok(new InlineCodeTestResponse(
                    false, null, f.ErrorMessage, logs, sw.ElapsedMilliseconds)),
                _ => Results.Ok(new InlineCodeTestResponse(
                    false, null, "Script suspended (WaitForEvent is not supported in test runs).", logs, sw.ElapsedMilliseconds)),
            };
        });
    }

    private static JsonElement? SerializeOutputs(Dictionary<string, object>? outputs)
    {
        if (outputs == null) return null;
        // 'selectedPort' is internal routing metadata, not user output — hide it from the test panel.
        var visible = outputs.Where(kv => kv.Key != "selectedPort").ToDictionary(kv => kv.Key, kv => kv.Value);
        return JsonSerializer.SerializeToElement(visible);
    }

    /// <summary>Collects a script's Logger output so the editor can show it in the test panel.</summary>
    private sealed class CapturingLogger : ILogger<InlineCodeNodeTask>
    {
        private readonly List<string> _logs;
        public CapturingLogger(List<string> logs) => _logs = logs;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception != null) message += $" — {exception.Message}";
            _logs.Add($"[{logLevel}] {message}");
        }
    }
}
