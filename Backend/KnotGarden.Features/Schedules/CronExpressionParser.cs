using System;
using Cronos;

namespace KnotGarden.Features.Schedules;

/// <summary>
/// Parses workflow cron expressions in the supported 5-field and 6-field formats.
/// </summary>
public static class CronExpressionParser
{
    /// <summary>
    /// Parses a cron expression using either standard minute precision or second precision.
    /// </summary>
    /// <param name="expression">The cron expression to parse.</param>
    /// <returns>The parsed cron expression.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="expression"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the expression does not have a supported shape or contains invalid cron syntax.</exception>
    public static CronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var partCount = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        try
        {
            return partCount switch
            {
                5 => CronExpression.Parse(expression),
                6 => CronExpression.Parse(expression, CronFormat.IncludeSeconds),
                _ => throw new InvalidOperationException(
                    "Cron expression must contain either 5 fields ('minute hour day-of-month month day-of-week') or 6 fields when including seconds.")
            };
        }
        catch (CronFormatException exception)
        {
            throw new InvalidOperationException(
                "Cron expression is invalid. Use either 5 fields ('minute hour day-of-month month day-of-week') or 6 fields when including seconds.",
                exception);
        }
    }
}