// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Knotarium.Features.Nodes.Sandbox;

public enum SandboxMode
{
    /// <summary>Run user code inside the backend process (today's trusted-author posture).</summary>
    InProcess,
    /// <summary>Run user code in pooled, OS-confined worker processes.</summary>
    Process
}

/// <summary>
/// Bound from <c>Security:Sandbox</c>. Governs where user-authored node code (inline code +
/// custom package source) executes and how hard the OS confines it. <c>AnalyzeAtRuntime</c>
/// from the same section is applied directly to <see cref="CSharpScriptCompiler"/> at startup.
/// </summary>
public sealed class SandboxOptions
{
    public const string SectionName = "Security:Sandbox";

    public SandboxMode Mode { get; set; } = SandboxMode.InProcess;

    /// <summary>Max pooled worker processes (also the max concurrently sandboxed node executions).</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>Hard per-worker memory cap enforced by the OS (Job Object / RLIMIT_AS).</summary>
    public int MemoryLimitMb { get; set; } = 512;

    /// <summary>CPU cap in percent of one core (Windows Job Object CPU rate control; best-effort).</summary>
    public int CpuPercent { get; set; } = 100;

    /// <summary>
    /// Grace period after a node's own timeout before the worker process is forcibly killed.
    /// The cooperative cancel gets this long to work; then the OS boundary takes over.
    /// </summary>
    public int KillGraceSeconds { get; set; } = 5;

    /// <summary>Recycle a worker after this many executions to bound leaked or pinned state.</summary>
    public int RecycleAfterRuns { get; set; } = 100;

    /// <summary>Hard ceiling applied when a node declares no timeout of its own (Process mode only).</summary>
    public int MaxRunSeconds { get; set; } = 300;

    /// <summary>
    /// Windows only: launch workers with a restricted token (privileges stripped, restricting
    /// SIDs, Low integrity level), denying access to user-/admin-scoped filesystem and registry
    /// objects. Best-effort: if the restricted launch API fails, the worker starts normally and
    /// a warning is logged. Disable for legacy node code that must touch the filesystem.
    /// </summary>
    public bool RestrictedToken { get; set; } = true;

    /// <summary>
    /// Process mode only: when true (default), secrets never enter the sandbox process.
    /// <c>GetSecretAsync</c> returns an opaque <c>{{knotarium-secret:ref}}</c> placeholder and
    /// the host substitutes the real value into proxied HTTP requests (URL, headers, textual
    /// body) just before sending. Node code can <i>use</i> a credential but never <i>read</i>
    /// it. Disable for legacy nodes that need the raw value for non-HTTP purposes (e.g. HMAC).
    /// </summary>
    public bool ProxyCredentials { get; set; } = true;

    /// <summary>Cap on a proxied HTTP response body marshalled back into the sandbox (Process mode).</summary>
    public int MaxHttpResponseMb { get; set; } = 32;

    public void Clamp()
    {
        WorkerCount = Math.Clamp(WorkerCount, 1, 32);
        MemoryLimitMb = Math.Clamp(MemoryLimitMb, 64, 16_384);
        CpuPercent = Math.Clamp(CpuPercent, 5, 100);
        KillGraceSeconds = Math.Clamp(KillGraceSeconds, 1, 60);
        RecycleAfterRuns = Math.Clamp(RecycleAfterRuns, 1, 10_000);
        MaxRunSeconds = Math.Clamp(MaxRunSeconds, 1, 3_600);
        MaxHttpResponseMb = Math.Clamp(MaxHttpResponseMb, 1, 100);
    }
}
