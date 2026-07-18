// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Knotarium.Features.Nodes.Sandbox;

/// <summary>
/// OS-level resource confinement for a sandbox worker process. Applied best-effort right
/// after spawn: a failure to confine is logged but does not abort the worker — the host's
/// hard kill-on-timeout still applies either way. Windows uses a Job Object (memory cap,
/// CPU rate, kill-on-close); Linux applies RLIMIT_AS via prlimit(2). cgroups v2 (the
/// stronger Linux backend) is a planned phase-2 refinement.
/// </summary>
public interface ISandboxConfinement : IDisposable
{
    void Apply(Process process);
}

public static class SandboxConfinementFactory
{
    public static ISandboxConfinement Create(SandboxOptions options, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsJobObjectConfinement(options, logger);
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxPrlimitConfinement(options, logger);
        }
        logger.LogWarning("Sandbox: no OS confinement backend for this platform; relying on host-enforced kill only.");
        return new NoConfinement();
    }

    private sealed class NoConfinement : ISandboxConfinement
    {
        public void Apply(Process process) { }
        public void Dispose() { }
    }
}

/// <summary>One Job Object per worker: memory cap, CPU rate cap, and kill-on-close so a dying host takes its workers with it.</summary>
internal sealed class WindowsJobObjectConfinement : ISandboxConfinement
{
    private readonly SandboxOptions _options;
    private readonly ILogger _logger;
    private nint _job;

    public WindowsJobObjectConfinement(SandboxOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public void Apply(Process process)
    {
        try
        {
            _job = CreateJobObjectW(0, null);
            if (_job == 0)
            {
                throw new InvalidOperationException($"CreateJobObject failed (error {Marshal.GetLastPInvokeError()}).");
            }

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_JOB_MEMORY | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                                 | JOB_OBJECT_LIMIT_ACTIVE_PROCESS,
                    // Exactly one process: user code cannot spawn children (Process.Start & co.
                    // fail with a quota error even if the API-level denylist were bypassed).
                    ActiveProcessLimit = 1
                },
                JobMemoryLimit = (nuint)_options.MemoryLimitMb * 1024 * 1024
            };
            if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                throw new InvalidOperationException($"SetInformationJobObject(limits) failed (error {Marshal.GetLastPInvokeError()}).");
            }

            // No desktop/clipboard/display/atom access — worker code has no UI business.
            var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = JOB_OBJECT_UILIMIT_ALL };
            if (!SetInformationJobObjectUi(_job, JobObjectBasicUIRestrictions, ref ui, Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>()))
            {
                _logger.LogWarning("Sandbox: UI restrictions could not be applied (error {Error}); process/memory limits still active.",
                    Marshal.GetLastPInvokeError());
            }

            if (_options.CpuPercent < 100)
            {
                var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                    // CpuRate is in 1/100ths of a percent of total system CPU.
                    CpuRate = (uint)_options.CpuPercent * 100 / (uint)Environment.ProcessorCount
                };
                if (cpu.CpuRate == 0)
                {
                    cpu.CpuRate = 100; // floor: 1% of total
                }
                if (!SetInformationJobObjectCpu(_job, JobObjectCpuRateControlInformation, ref cpu, Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
                {
                    _logger.LogWarning("Sandbox: CPU rate cap could not be applied (error {Error}); memory cap still active.",
                        Marshal.GetLastPInvokeError());
                }
            }

            if (!AssignProcessToJobObject(_job, process.Handle))
            {
                throw new InvalidOperationException($"AssignProcessToJobObject failed (error {Marshal.GetLastPInvokeError()}).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sandbox: Job Object confinement could not be applied to worker {Pid}; " +
                "host-enforced kill remains active.", process.Id);
        }
    }

    public void Dispose()
    {
        if (_job != 0)
        {
            // Kill-on-close: closing the handle terminates any process still in the job.
            CloseHandle(_job);
            _job = 0;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectBasicUIRestrictions = 4;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_UILIMIT_ALL = 0xFF;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_UI_RESTRICTIONS
    {
        public uint UIRestrictionsClass;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObjectCpu(nint job, int infoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, int length);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObjectUi(nint job, int infoClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>
/// Linux confinement: prefers a per-worker cgroup v2 (memory.max, cpu.max, pids.max — requires
/// cgroup delegation, typically available inside containers or under systemd user slices) and
/// falls back to prlimit(RLIMIT_AS) when the cgroup filesystem is not writable.
/// </summary>
internal sealed class LinuxPrlimitConfinement : ISandboxConfinement
{
    private const string CgroupRoot = "/sys/fs/cgroup";

    private readonly SandboxOptions _options;
    private readonly ILogger _logger;
    private string? _cgroupDir;

    public LinuxPrlimitConfinement(SandboxOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public void Apply(Process process)
    {
        if (TryApplyCgroup(process))
        {
            return;
        }

        var bytes = (ulong)_options.MemoryLimitMb * 1024 * 1024;
        var limit = new RLimit { Current = bytes, Maximum = bytes };
        if (prlimit(process.Id, RLIMIT_AS, ref limit, 0) != 0)
        {
            _logger.LogWarning("Sandbox: prlimit(RLIMIT_AS) failed for worker {Pid} (errno {Errno}); " +
                "host-enforced kill remains active.", process.Id, Marshal.GetLastPInvokeError());
        }
        if (_options.CpuPercent < 100)
        {
            _logger.LogInformation("Sandbox: CPU-percent capping on Linux needs a writable cgroup v2 hierarchy; only the memory cap is active.");
        }
    }

    private bool TryApplyCgroup(Process process)
    {
        try
        {
            if (!System.IO.Directory.Exists(CgroupRoot))
            {
                return false;
            }

            var dir = System.IO.Path.Combine(CgroupRoot, $"knotarium-sbx-{process.Id}");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "memory.max"),
                ((ulong)_options.MemoryLimitMb * 1024 * 1024).ToString());
            if (_options.CpuPercent < 100)
            {
                // cpu.max = "<quota> <period>" in microseconds; percent of one core.
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "cpu.max"),
                    $"{_options.CpuPercent * 1000} 100000");
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "pids.max"), "16");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "cgroup.procs"), process.Id.ToString());
            _cgroupDir = dir;
            _logger.LogInformation("Sandbox: worker {Pid} confined via cgroup {Dir}.", process.Id, dir);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sandbox: cgroup v2 confinement unavailable; falling back to prlimit.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_cgroupDir is not null)
        {
            try
            {
                // Succeeds only once the (killed) worker has fully exited and the group is empty.
                System.IO.Directory.Delete(_cgroupDir);
            }
            catch
            {
                // best-effort; leftover empty cgroups are harmless and reusable names are pid-based
            }
            _cgroupDir = null;
        }
    }

    private const int RLIMIT_AS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prlimit(int pid, int resource, ref RLimit newLimit, nint oldLimit);
}
