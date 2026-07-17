// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Reads a local file and emits its contents. <c>encoding=utf8</c> (default) returns text; <c>base64</c>
/// returns the raw bytes base64-encoded, so binary files travel through the JSON payload safely. Emits
/// <c>result = { content, encoding, size, path }</c>.
/// <para>Every read is validated against the instance-global <see cref="Core.Domain.FileAccessPolicy"/> via
/// <see cref="IFileAccessPolicy"/> before any IO — the guard also returns the canonical, boundary-checked
/// path actually used, so the node never touches the raw input path.</para>
/// </summary>
public class FileReadNodeTask : INodeTask
{
    private readonly IFileAccessPolicy _fileAccess;

    public FileReadNodeTask(IFileAccessPolicy fileAccess)
    {
        _fileAccess = fileAccess;
    }

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = context.Inputs.TryGetValue("path", out var p) ? p?.ToString() : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new LegacyNodeResult.Failure("File read failed: missing required 'path'.");
        }

        var encoding = (context.Inputs.TryGetValue("encoding", out var e) ? e?.ToString() : null)?.Trim().ToLowerInvariant();
        encoding = encoding is "base64" ? "base64" : "utf8";

        var decision = await _fileAccess.CheckReadAsync(path, cancellationToken);
        if (!decision.Allowed)
        {
            // Tagged distinctly (vs an ordinary IO failure) so the UI can offer a "request access" action.
            return new LegacyNodeResult.Failure($"File read failed: {decision.DenyReason}", ErrorCode: "FileAccessDenied");
        }
        var safePath = decision.CanonicalPath!;

        try
        {
            if (!System.IO.File.Exists(safePath))
            {
                return new LegacyNodeResult.Failure($"File read failed: file not found at '{safePath}'.");
            }

            var bytes = await System.IO.File.ReadAllBytesAsync(safePath, cancellationToken);
            object content = encoding == "base64" ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);

            var result = new Dictionary<string, object>
            {
                ["content"] = content,
                ["encoding"] = encoding,
                ["size"] = bytes.Length,
                ["path"] = safePath,
            };
            return new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = result });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"File read failed: {ex.Message}");
        }
    }
}
