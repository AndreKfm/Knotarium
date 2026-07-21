// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Domain;

/// <summary>
/// A single global, runtime-mutable, persisted application setting stored as a key/value row.
/// Unlike <c>RuntimeArmingState</c> (in-memory only), these survive restarts. Used for global
/// configuration that has no natural home on another entity — e.g. the default error workflow id.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

/// <summary>Well-known <see cref="AppSetting.Key"/> values.</summary>
public static class AppSettingKeys
{
    /// <summary>Workflow definition id of the global error-handler workflow run when any workflow fails.</summary>
    public const string DefaultErrorWorkflowId = "DefaultErrorWorkflowId";

    /// <summary>JSON blob holding the active AI provider configuration (vendor, model, base url, credential ref).</summary>
    public const string AiProviderConfig = "AiProviderConfig";

    /// <summary>JSON blob holding the global file-access policy (permitted path grants, total-access flag,
    /// free-space reserve) enforced by the file nodes. Unset ⇒ deny-by-default.</summary>
    public const string FileAccessPolicy = "FileAccessPolicy";

    /// <summary>JSON blob listing the enabled privileged node capabilities (e.g. code execution, database).
    /// Unset ⇒ all switchable capabilities off.</summary>
    public const string CapabilityPolicy = "CapabilityPolicy";

    /// <summary>JSON blob holding the operator's sandbox configuration for user-authored node code
    /// (execution mode, worker limits, restricted token, credential proxying). Unset ⇒ the
    /// Security:Sandbox configuration section (and its secure defaults) applies unchanged.</summary>
    public const string SandboxSettings = "SandboxSettings";

    /// <summary>Persisted runtime arming state ("true"/"false"): the last value set explicitly via the
    /// arming endpoint, restored on startup so an armed instance stays armed across restarts. Unset ⇒
    /// fall back to the "Runtime:Armed" configuration value (default: disarmed). Transient safety
    /// disarms (e.g. the disk-space guard) deliberately do NOT write this key.</summary>
    public const string RuntimeArmed = "RuntimeArmed";

    /// <summary>JSON blob holding the data-retention policy (run-history/log days, sweep interval,
    /// version-history and audit-log caps) that bounds database growth. Unset ⇒ the "Retention"
    /// configuration section (and its defaults) applies unchanged.</summary>
    public const string RetentionPolicy = "RetentionPolicy";
}
