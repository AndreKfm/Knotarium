// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.IO.Pipes;

namespace Knotarium.SandboxWorker;

/// <summary>
/// Sandbox worker process: executes user-authored node code on behalf of the Knotarium host.
/// One worker handles one execution at a time (the host's pool guarantees this). The host is
/// the security authority — this process is expected to be confined from the outside (Job
/// Object / prlimit) and may be killed at any moment; it holds no state worth preserving.
/// (An explicit Main class, not top-level statements: the host references this assembly for
/// build/copy ordering, and a generated global "Program" would clash with the host's own.)
/// </summary>
internal static class WorkerProgram
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: Knotarium.SandboxWorker <pipeName>");
            return 2;
        }

        var pipeName = args[0];

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10_000);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Sandbox worker could not connect to pipe '{pipeName}' within 10s.");
            return 3;
        }

        var session = new WorkerSession(pipe);
        await session.RunAsync();
        return 0;
    }
}
