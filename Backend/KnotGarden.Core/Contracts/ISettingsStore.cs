namespace KnotGarden.Core.Contracts;

/// <summary>
/// Raw key/value access to instance-wide settings (the <c>AppSettings</c> table). Feature code that
/// reads or writes global settings depends on this seam instead of the shared EF context, so the
/// Settings slice can live outside the persistence assembly. The domain-flavoured accessors (e.g. the
/// default error-workflow id) live in the Settings slice on top of this store.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Reads a global setting value by key, or null when unset.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Upserts (or clears, when <paramref name="value"/> is null) a global setting value.</summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}
