using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;

namespace KnotGarden.Features.Nodes;

/// <summary>
/// Built-in "Inline Code" node: runs a short script typed directly into the node's
/// <c>code</c> parameter, filling the gap between the read-only ExpressionEvaluator and
/// authoring a full custom node package. v1 supports C# only (compiled via the shared
/// <see cref="CSharpScriptCompiler"/>); the <c>language</c> parameter is the seam for
/// adding JavaScript later.
///
/// The script body is wrapped by <see cref="CSharpScriptCompiler"/>, so it has access to
/// <c>Input.Get&lt;T&gt;(name)</c>, <c>Logger</c>, <c>cancellationToken</c>, and the
/// <c>Success(obj)</c> / <c>Fail(msg)</c> helpers. Whatever object the script returns via
/// <c>Success(...)</c> becomes the node's outputs.
///
/// Execution is <b>not sandboxed</b> (trusted-author posture) and is bounded by a timeout.
/// </summary>
public sealed class InlineCodeNodeTask : INodeTask
{
    public const int TimeoutSeconds = KnotGarden.Core.Domain.InlineCodeNodeDefaults.TimeoutSeconds;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialAccessor _credentialAccessor;
    private readonly ILogger<InlineCodeNodeTask> _logger;
    private readonly CSharpScriptCompiler _compiler;
    private readonly ICapabilityPolicy _capabilities;
    private readonly int _timeoutSeconds;

    public InlineCodeNodeTask(
        IHttpClientFactory httpClientFactory,
        ICredentialAccessor credentialAccessor,
        ILogger<InlineCodeNodeTask> logger,
        CSharpScriptCompiler compiler,
        ICapabilityPolicy capabilities,
        int? timeoutSeconds = null)
    {
        _httpClientFactory = httpClientFactory;
        _credentialAccessor = credentialAccessor;
        _logger = logger;
        _compiler = compiler;
        _capabilities = capabilities;
        _timeoutSeconds = timeoutSeconds ?? TimeoutSeconds;
    }

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // Arbitrary code execution is a privileged capability, off unless an admin enables it.
        if (!await _capabilities.IsEnabledAsync(KnotGarden.Core.Domain.NodeCapabilities.CodeExecution, cancellationToken))
        {
            return new LegacyNodeResult.Failure(
                "Inline Code is disabled: the 'code execution' capability is off. An administrator can enable it under Settings → Capabilities.");
        }

        var language = (GetStringInput(context, "language") ?? "csharp").Trim().ToLowerInvariant();
        if (language is not ("csharp" or "c#" or ""))
        {
            return new LegacyNodeResult.Failure(
                $"Inline Code: language '{language}' is not supported in v1; only 'csharp' is available.");
        }

        var code = GetStringInput(context, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            return new LegacyNodeResult.Failure("Inline Code: no script provided.");
        }

        // Compile (cached by source hash so identical scripts compile once per process).
        Type executorType;
        try
        {
            executorType = _compiler.GetOrCompile(HashKey(code), code);
        }
        catch (ScriptCompilationException ex)
        {
            return new LegacyNodeResult.Failure($"Inline Code compilation failed:\n{ex.Message}");
        }

        var executor = _compiler.Instantiate(executorType);

        // Bound execution with a timeout. Note: cooperative — a script must observe
        // cancellationToken (e.g. await Task.Delay(..., cancellationToken)) for a long
        // operation to be interrupted.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            var result = await _compiler.RunAsync(
                executor, context, _httpClientFactory, _credentialAccessor, _logger, extraInputs: null, cts.Token);

            // The script wrapper catches its own exceptions (including the cancellation), so a
            // timeout may surface as a Failure rather than a thrown OperationCanceledException.
            // Detect it from the token state and report it as a timeout.
            if (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new LegacyNodeResult.Failure($"Inline Code timed out after {_timeoutSeconds}s.");
            }
            return result;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new LegacyNodeResult.Failure($"Inline Code timed out after {_timeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Inline Code execution threw an exception: {ex.Message}");
        }
    }

    private static string? GetStringInput(NodeExecutionContext context, string name)
    {
        if (!context.Inputs.TryGetValue(name, out var raw) || raw is null)
            return null;
        if (raw is string s)
            return s;
        if (raw is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
        return raw.ToString();
    }

    private static string HashKey(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return "inlineCode_" + Convert.ToHexString(bytes);
    }
}
