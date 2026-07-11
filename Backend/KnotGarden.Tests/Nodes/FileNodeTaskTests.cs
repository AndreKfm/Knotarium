using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Nodes;
using Xunit;

namespace KnotGarden.Tests.Nodes;

public class FileNodeTaskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kg-filenode-" + Guid.NewGuid().ToString("N"));

    // These tests exercise the IO behaviour of the file nodes; the guard is configured to grant the temp
    // directory read+write so enforcement is satisfied. The guard's own decision logic (deny-by-default,
    // traversal/symlink escapes, mode, free space) is covered separately in FileAccessGuardTests.
    private readonly IFileAccessPolicy _fileAccess;

    private sealed class StubPolicyProvider : IFileAccessPolicyProvider
    {
        private readonly FileAccessPolicy _policy;
        public StubPolicyProvider(FileAccessPolicy policy) => _policy = policy;
        public Task<FileAccessPolicy> GetPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_policy);
    }

    public FileNodeTaskTests()
    {
        Directory.CreateDirectory(_dir);
        _fileAccess = new FileAccessGuard(new StubPolicyProvider(new FileAccessPolicy(
            TotalAccess: false,
            Rules: new[] { new FileAccessRule(_dir, FileAccessMode.ReadWrite) },
            MinFreeBytes: null,
            MinFreePercent: null)));
    }

    private FileReadNodeTask ReadTask() => new(_fileAccess);
    private FileWriteNodeTask WriteTask() => new(_fileAccess);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private static NodeExecutionContext Context(Dictionary<string, object> inputs) => new(
        WorkflowId: WorkflowDefinitionId.New(),
        ExecutionId: Guid.NewGuid(),
        NodeId: NodeId.Create("file-1"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    private static Dictionary<string, object?> Result(LegacyNodeResult result)
    {
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        return (Dictionary<string, object>)success.Outputs!["result"];
    }

    [Fact]
    public async Task Write_then_read_round_trips_utf8_text()
    {
        var path = Path.Combine(_dir, "note.txt");

        var write = await WriteTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = path,
            ["content"] = "hello world",
        }), CancellationToken.None);
        Assert.Equal(11, Convert.ToInt32(Result(write)["bytesWritten"]));

        var read = await ReadTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = path,
        }), CancellationToken.None);
        var payload = Result(read);
        Assert.Equal("hello world", payload["content"]);
        Assert.Equal("utf8", payload["encoding"]);
    }

    [Fact]
    public async Task Base64_round_trips_binary_bytes()
    {
        var path = Path.Combine(_dir, "blob.bin");
        var bytes = new byte[] { 0, 1, 2, 250, 255 };
        var b64 = Convert.ToBase64String(bytes);

        await WriteTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = path, ["content"] = b64, ["encoding"] = "base64",
        }), CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        var read = await ReadTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = path, ["encoding"] = "base64",
        }), CancellationToken.None);
        Assert.Equal(b64, Result(read)["content"]);
    }

    [Fact]
    public async Task Append_adds_to_existing_file_and_creates_missing_dirs()
    {
        var path = Path.Combine(_dir, "nested", "log.txt");

        await WriteTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = path, ["content"] = "a",
        }), CancellationToken.None);
        await WriteTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = path, ["content"] = "b", ["append"] = true,
        }), CancellationToken.None);

        Assert.Equal("ab", await File.ReadAllTextAsync(path, Encoding.UTF8));
    }

    [Fact]
    public async Task Read_missing_file_fails()
    {
        var result = await ReadTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = Path.Combine(_dir, "nope.txt"),
        }), CancellationToken.None);
        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    [Fact]
    public async Task Write_invalid_base64_fails()
    {
        var result = await WriteTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = Path.Combine(_dir, "x.bin"), ["content"] = "not base64!!", ["encoding"] = "base64",
        }), CancellationToken.None);
        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    [Fact]
    public async Task Write_to_existing_directory_path_fails_with_clear_message()
    {
        var result = await WriteTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = _dir, ["content"] = "x", // _dir is a directory, not a file
        }), CancellationToken.None);
        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("is a directory", failure.ErrorMessage);
    }

    [Fact]
    public async Task Write_to_trailing_separator_path_fails_with_clear_message()
    {
        var result = await WriteTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = Path.Combine(_dir, "sub") + Path.DirectorySeparatorChar, ["content"] = "x",
        }), CancellationToken.None);
        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("directory separator", failure.ErrorMessage);
    }

    [Fact]
    public async Task Write_to_extensionless_filename_succeeds()
    {
        var path = Path.Combine(_dir, "noext"); // a file named 'noext', not a folder
        var result = await WriteTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["path"] = path, ["content"] = "hi",
        }), CancellationToken.None);
        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("hi", await File.ReadAllTextAsync(path));
    }
}
