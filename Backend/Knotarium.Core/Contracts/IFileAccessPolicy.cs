using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Guard the built-in file nodes call before touching the disk. It fetches the current
/// <see cref="FileAccessPolicy"/>, canonicalizes the requested path (defeating traversal / symlink escapes),
/// and confirms the operation is permitted — returning the resolved path to use, or a deny reason.
/// </summary>
public interface IFileAccessPolicy
{
    /// <summary>Validate a read of <paramref name="path"/>.</summary>
    Task<FileAccessResult> CheckReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate a write of <paramref name="bytesToWrite"/> bytes to <paramref name="path"/>. When
    /// <paramref name="append"/> is true the bytes are added to any existing file; otherwise they replace it.
    /// Enforces both the path grant and the free-space reserve of the target drive.
    /// </summary>
    Task<FileAccessResult> CheckWriteAsync(string path, long bytesToWrite, bool append, CancellationToken cancellationToken = default);
}

/// <summary>Supplies the current instance-global <see cref="FileAccessPolicy"/> to the guard.</summary>
public interface IFileAccessPolicyProvider
{
    Task<FileAccessPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);
}
