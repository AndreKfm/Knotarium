// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;

namespace Knotarium.Api.Services;

/// <summary>
/// Global runtime "armed" switch that separates design-time from run-time.
/// <para>
/// When <see cref="IsArmed"/> is <see langword="false"/> (disarmed / editing mode),
/// the <see cref="SchedulingWorker"/> pauses automatic schedule evaluation so nothing
/// fires on its own. Manual triggers (e.g. "Run") always execute regardless of this flag.
/// </para>
/// <para>
/// The initial value is seeded from configuration ("Runtime:Armed") so the server can be
/// started armed headlessly for production, while the UI can flip it at runtime.
/// </para>
/// </summary>
public sealed class RuntimeArmingState : IRuntimeArmingState
{
    private volatile bool _armed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeArmingState"/> class.
    /// </summary>
    /// <param name="initiallyArmed">The initial armed state, typically seeded from configuration.</param>
    public RuntimeArmingState(bool initiallyArmed)
    {
        _armed = initiallyArmed;
    }

    /// <summary>
    /// Gets a value indicating whether automatic schedule evaluation is currently armed.
    /// </summary>
    public bool IsArmed => _armed;

    /// <summary>
    /// Sets the armed state.
    /// </summary>
    /// <param name="armed"><see langword="true"/> to arm automatic execution; otherwise, <see langword="false"/>.</param>
    public void SetArmed(bool armed)
    {
        _armed = armed;
    }
}
