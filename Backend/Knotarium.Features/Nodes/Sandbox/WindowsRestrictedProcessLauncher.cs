// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Knotarium.Features.Nodes.Sandbox;

/// <summary>
/// Launches the sandbox worker with a <b>restricted token</b> derived from the host's own
/// primary token (the documented case where CreateProcessAsUser needs no special privilege):
/// <list type="bullet">
/// <item>all privileges stripped (<c>DISABLE_MAX_PRIVILEGE</c>),</item>
/// <item>restricting SIDs (Everyone, Users, Authenticated Users, the logon session) — an access
/// check must now pass against BOTH the normal DACL and this list, so objects granted only to
/// the specific user or to Administrators (user profiles, service data directories) are denied
/// while world/Users-readable binaries and framework files keep working,</item>
/// <item>Low integrity level — Windows' no-write-up rule blocks writes to any normal (Medium+)
/// filesystem or registry object regardless of DACL.</item>
/// </list>
/// The process starts <b>suspended</b> so the Job Object is in place before user code can run;
/// the caller resumes it after confinement is applied.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRestrictedProcessLauncher
{
    /// <summary>
    /// Stamps a Low-integrity mandatory label onto a kernel object (the worker pipe). Setting a
    /// label needs no privilege — unlike creating the object with a SACL in one step, which
    /// requires SeSecurityPrivilege and fails for non-admin hosts.
    /// </summary>
    public static void AllowLowIntegrityAccess(Microsoft.Win32.SafeHandles.SafePipeHandle handle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                "S:(ML;;NW;;;LW)", SDDL_REVISION_1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SDDL(Low label) conversion failed.");
        }
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present)
            {
                throw new Win32Exception("Low label descriptor carries no SACL.");
            }
            var status = SetSecurityInfo(handle.DangerousGetHandle(), SE_KERNEL_OBJECT,
                LABEL_SECURITY_INFORMATION, 0, 0, 0, sacl);
            if (status != 0)
            {
                throw new Win32Exception((int)status, "SetSecurityInfo(Low label) failed.");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>Starts the worker restricted and suspended. Throws Win32Exception on failure —
    /// the caller decides whether to fall back to a normal launch.</summary>
    public static (Process Process, Action Resume) Start(
        string exePath, string arguments, string workingDirectory,
        bool lowIntegrity = true, bool restrictingSids = true)
    {
        nint processToken = 0, restrictedToken = 0;
        var sidsToFree = new System.Collections.Generic.List<nint>();
        nint hThread = 0, hProcess = 0;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out processToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");
            }

            // Restricting SIDs: world-scoped groups plus the logon session (needed for window
            // station/desktop access). Attributes must be 0 for restricting entries.
            var restricting = new System.Collections.Generic.List<SID_AND_ATTRIBUTES>();
            foreach (var sddl in new[] { "S-1-1-0" /* Everyone */, "S-1-5-32-545" /* Users */, "S-1-5-11" /* Authenticated Users */ })
            {
                if (!ConvertStringSidToSidW(sddl, out var sid))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"ConvertStringSidToSid({sddl}) failed.");
                }
                sidsToFree.Add(sid);
                restricting.Add(new SID_AND_ATTRIBUTES { Sid = sid });
            }
            var logonSid = FindLogonSid(processToken);
            if (logonSid != 0)
            {
                restricting.Add(new SID_AND_ATTRIBUTES { Sid = logonSid }); // freed with the buffer below
            }

            var restrictingArray = restrictingSids ? restricting.ToArray() : Array.Empty<SID_AND_ATTRIBUTES>();
            if (!CreateRestrictedToken(processToken, DISABLE_MAX_PRIVILEGE,
                    0, null, 0, null,
                    (uint)restrictingArray.Length, restrictingArray.Length > 0 ? restrictingArray : null, out restrictedToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRestrictedToken failed.");
            }

            if (lowIntegrity)
            {
                SetLowIntegrityLevel(restrictedToken, sidsToFree);
            }

            var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
            var commandLine = $"\"{exePath}\" {arguments}";
            // DETACHED_PROCESS, not CREATE_NO_WINDOW: the hidden console the latter creates needs
            // a ConDrv connection that the restricting-SID access check denies, killing the child
            // during startup. A fully detached worker has no console at all — Console.* no-op.
            if (!CreateProcessAsUserW(restrictedToken, null, commandLine, 0, 0, false,
                    CREATE_SUSPENDED | DETACHED_PROCESS, 0, workingDirectory, ref si, out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser(restricted) failed.");
            }

            hThread = pi.hThread;
            hProcess = pi.hProcess;
            var process = Process.GetProcessById((int)pi.dwProcessId);
            var resumeThread = hThread;
            hThread = 0; // ownership moves to the resume closure
            return (process, () =>
            {
                ResumeThread(resumeThread);
                CloseHandle(resumeThread);
            });
        }
        finally
        {
            if (hThread != 0) CloseHandle(hThread);
            if (hProcess != 0) CloseHandle(hProcess);
            if (restrictedToken != 0) CloseHandle(restrictedToken);
            if (processToken != 0) CloseHandle(processToken);
            foreach (var sid in sidsToFree) LocalFree(sid);
        }
    }

    /// <summary>Finds the token's logon-session SID (SE_GROUP_LOGON_ID). Returns 0 when absent
    /// (e.g. some service contexts) — the launch then simply omits it.</summary>
    private static nint FindLogonSid(nint token)
    {
        GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenGroups, 0, 0, out var needed);
        if (needed == 0)
        {
            return 0;
        }
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenGroups, buffer, needed, out _))
            {
                return 0;
            }
            var count = Marshal.ReadInt32(buffer);
            var entryStart = buffer + nint.Size; // GroupCount padded to pointer size
            var entrySize = Marshal.SizeOf<SID_AND_ATTRIBUTES>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<SID_AND_ATTRIBUTES>(entryStart + i * entrySize);
                if ((entry.Attributes & SE_GROUP_LOGON_ID) == SE_GROUP_LOGON_ID)
                {
                    // Copy: the source buffer is freed on return.
                    var length = GetLengthSid(entry.Sid);
                    var copy = Marshal.AllocHGlobal((int)length);
                    if (!CopySid(length, copy, entry.Sid))
                    {
                        Marshal.FreeHGlobal(copy);
                        return 0;
                    }
                    _logonSidCopy = copy; // freed on process exit; one per launch is negligible
                    return copy;
                }
            }
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static nint _logonSidCopy;

    private static void SetLowIntegrityLevel(nint token, System.Collections.Generic.List<nint> sidsToFree)
    {
        if (!ConvertStringSidToSidW("S-1-16-4096" /* Low IL */, out var lowSid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSidToSid(Low IL) failed.");
        }
        sidsToFree.Add(lowSid);

        var label = new TOKEN_MANDATORY_LABEL
        {
            Label = new SID_AND_ATTRIBUTES { Sid = lowSid, Attributes = SE_GROUP_INTEGRITY }
        };
        var size = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + (int)GetLengthSid(lowSid);
        if (!SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, ref label, (uint)size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(Low IL) failed.");
        }
    }

    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint SDDL_REVISION_1 = 1;
    private const int SE_KERNEL_OBJECT = 6;
    private const uint LABEL_SECURITY_INFORMATION = 0x00000010;
    private const uint DISABLE_MAX_PRIVILEGE = 0x1;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint DETACHED_PROCESS = 0x00000008;
    private const uint SE_GROUP_LOGON_ID = 0xC0000000;
    private const uint SE_GROUP_INTEGRITY = 0x00000020;

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenGroups = 2,
        TokenIntegrityLevel = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(
        nint existingToken, uint flags,
        uint disableSidCount, SID_AND_ATTRIBUTES[]? sidsToDisable,
        uint deletePrivilegeCount, nint[]? privilegesToDelete,
        uint restrictedSidCount, SID_AND_ATTRIBUTES[]? sidsToRestrict,
        out nint newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string stringSid, out nint sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token, TOKEN_INFORMATION_CLASS infoClass, nint info, uint infoLength, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(
        nint token, TOKEN_INFORMATION_CLASS infoClass, ref TOKEN_MANDATORY_LABEL info, uint infoLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        nint token, string? applicationName, string? commandLine,
        nint processAttributes, nint threadAttributes, bool inheritHandles, uint creationFlags,
        nint environment, string? currentDirectory, ref STARTUPINFOW startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint mem);

    [DllImport("advapi32.dll")]
    private static extern uint GetLengthSid(nint sid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out nint descriptor, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(
        nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool saclPresent, out nint sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool saclDefaulted);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(
        nint handle, int objectType, uint securityInfo, nint owner, nint group, nint dacl, nint sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopySid(uint destLength, nint dest, nint source);
}
