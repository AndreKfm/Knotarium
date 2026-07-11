using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Settings;

/// <summary>
/// Read/write access to global <see cref="AppSetting"/> values. Backed by the Core
/// <see cref="ISettingsStore"/> seam (an EF adapter in the host) so both the API (settings endpoints)
/// and the error-workflow worker read through a single source of truth for the global default error
/// workflow id without this slice binding the shared DbContext.
/// </summary>
public sealed class GlobalSettingsService
{
    private readonly ISettingsStore _store;

    public GlobalSettingsService(ISettingsStore store)
    {
        _store = store;
    }

    /// <summary>Returns the configured default error workflow id, or null when none is set.</summary>
    public async Task<string?> GetDefaultErrorWorkflowIdAsync(CancellationToken cancellationToken = default)
    {
        var value = await _store.GetAsync(AppSettingKeys.DefaultErrorWorkflowId, cancellationToken);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Sets (or clears, when <paramref name="workflowId"/> is null/blank) the default error workflow id.</summary>
    public Task SetDefaultErrorWorkflowIdAsync(string? workflowId, CancellationToken cancellationToken = default)
        => _store.SetAsync(AppSettingKeys.DefaultErrorWorkflowId,
                    string.IsNullOrWhiteSpace(workflowId) ? null : workflowId,
                    cancellationToken);

    /// <summary>Reads any global <see cref="AppSetting"/> value by key (null when unset).</summary>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => _store.GetAsync(key, cancellationToken);

    /// <summary>Upserts (or clears, when <paramref name="value"/> is null) any global <see cref="AppSetting"/> value.</summary>
    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        => _store.SetAsync(key, value, cancellationToken);
}
