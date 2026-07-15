using System.Collections.Generic;
using System.Linq;

namespace Knotarium.Core.Domain;

/// <summary>
/// Well-known capability tags carried by a node manifest's <c>Capabilities</c> list. These declare the
/// privileged resources a node touches, so the host can reason about risk (e.g. warn before installing an
/// imported workflow that reads the filesystem, or — later — gate a node behind a per-role grant).
/// <para>
/// Today the only tag that is actively <b>enforced</b> is filesystem access (the file nodes consult the
/// <see cref="FileAccessPolicy"/>). The rest are descriptive metadata that the multi-user capability model
/// will build on. Kept as plain strings because the list is an open vocabulary shared with binary/custom
/// node packages.
/// </para>
/// </summary>
public static class NodeCapabilities
{
    /// <summary>Reads local files (enforced against the <see cref="FileAccessPolicy"/>).</summary>
    public const string FilesystemRead = "filesystem.read";

    /// <summary>Writes local files (enforced against the <see cref="FileAccessPolicy"/>).</summary>
    public const string FilesystemWrite = "filesystem.write";

    /// <summary>Executes arbitrary code on the host (e.g. the inline-code node). Highest privilege.</summary>
    public const string CodeExecution = "code.execute";

    /// <summary>Runs arbitrary database queries.</summary>
    public const string Database = "database";

    /// <summary>Makes outbound network calls. Pre-existing informal tag.</summary>
    public const string Network = "network";

    /// <summary>Resolves stored credentials/secrets. Pre-existing informal tag.</summary>
    public const string Credentials = "credentials";

    /// <summary>
    /// Runs an LLM tool-use loop that can invoke other workflows as tools (the <c>aiAgent</c> node).
    /// Enforced like <see cref="CodeExecution"/>/<see cref="Database"/>: off unless an admin enables it.
    /// </summary>
    public const string AiAgent = "aiAgent";

    /// <summary>
    /// The security-sensitive capabilities worth flagging before installing an imported workflow
    /// (template / bundle): filesystem, code execution, database, and the AI agent loop. A node carrying
    /// any of these lets an imported or AI-generated graph touch the host beyond ordinary data flow.
    /// </summary>
    public static readonly IReadOnlyList<string> Privileged = new[]
    {
        FilesystemRead,
        FilesystemWrite,
        CodeExecution,
        Database,
        AiAgent,
    };

    /// <summary>True if <paramref name="capability"/> is one of the <see cref="Privileged"/> tags.</summary>
    public static bool IsPrivileged(string capability) =>
        Privileged.Any(c => string.Equals(c, capability, System.StringComparison.OrdinalIgnoreCase));
}
