using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Writes content to a local file. <c>encoding=utf8</c> (default) writes the text as-is; <c>base64</c>
/// decodes the content to raw bytes first (for binary payloads). <c>append=true</c> appends instead of
/// overwriting; missing parent directories are created. Emits <c>result = { path, bytesWritten }</c>.
/// <para>Every write is validated against the instance-global <see cref="Core.Domain.FileAccessPolicy"/> via
/// <see cref="IFileAccessPolicy"/> before any IO: the target must sit inside a write-granted directory and
/// leave the configured free-space reserve on the drive. The guard returns the canonical, boundary-checked
/// path actually written.</para>
/// </summary>
public class FileWriteNodeTask : INodeTask
{
    private readonly IFileAccessPolicy _fileAccess;

    public FileWriteNodeTask(IFileAccessPolicy fileAccess)
    {
        _fileAccess = fileAccess;
    }

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var path = context.Inputs.TryGetValue("path", out var p) ? p?.ToString() : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new LegacyNodeResult.Failure("File write failed: missing required 'path'.");
        }

        var content = context.Inputs.TryGetValue("content", out var c) ? c?.ToString() ?? string.Empty : string.Empty;
        var encoding = (context.Inputs.TryGetValue("encoding", out var e) ? e?.ToString() : null)?.Trim().ToLowerInvariant();
        var append = context.Inputs.TryGetValue("append", out var a) && a is not null
            && bool.TryParse(a.ToString(), out var appendFlag) && appendFlag;

        byte[] bytes;
        try
        {
            bytes = encoding is "base64" ? Convert.FromBase64String(content) : Encoding.UTF8.GetBytes(content);
        }
        catch (FormatException)
        {
            return new LegacyNodeResult.Failure("File write failed: content is not valid base64.");
        }

        var decision = await _fileAccess.CheckWriteAsync(path, bytes.Length, append, cancellationToken);
        if (!decision.Allowed)
        {
            // Tagged distinctly (vs an ordinary IO failure) so the UI can offer a "request access" action.
            return new LegacyNodeResult.Failure($"File write failed: {decision.DenyReason}", ErrorCode: "FileAccessDenied");
        }
        var fullPath = decision.CanonicalPath!;

        // Reject the two folder-shaped inputs up front with a clear message — otherwise they surface as a
        // raw OS "Access to the path '…' is denied", which reads like a permission block now that a
        // file-access policy exists. A path ending in a separator (d:\test\) names a folder with no filename;
        // a path that resolves to an existing directory (d:\test) is likewise not a file. Anything else —
        // including an extensionless name whose folder doesn't exist (d:\test → file 'test' in d:\) — writes.
        if (Path.EndsInDirectorySeparator(path))
        {
            return new LegacyNodeResult.Failure(
                $"File write failed: the path ends in a directory separator, so it names a folder — add a filename, e.g. {Path.Combine(path.TrimEnd('\\', '/'), "output.txt")}.");
        }
        if (Directory.Exists(fullPath))
        {
            return new LegacyNodeResult.Failure(
                $"File write failed: '{fullPath}' is a directory, not a file. Set 'path' to a file inside it, e.g. {Path.Combine(fullPath, "output.txt")}.");
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(fullPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
            {
                await stream.WriteAsync(bytes, cancellationToken);
            }

            var result = new Dictionary<string, object>
            {
                ["path"] = fullPath,
                ["bytesWritten"] = bytes.Length,
            };
            return new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = result });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"File write failed: {ex.Message}");
        }
    }
}
