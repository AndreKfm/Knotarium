using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Knotarium.Features.Portability;

namespace Knotarium.Features.Templates;

/// <summary>Raised when declared parameters or supplied parameter values are invalid. Carries every error at once.</summary>
public sealed class TemplateParameterException(IReadOnlyList<string> errors)
    : InvalidOperationException("The template parameters are invalid.")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Turns the author's declared <see cref="TemplateParameter"/>s plus the installer's raw string values into
/// the typed, ready-to-substitute <see cref="ParameterValue"/> map consumed by
/// <see cref="CredentialSlotModule.SubstituteParameters"/>. All validation lives here (fail-fast with the
/// <em>full</em> error list) so that substitution itself is total and cannot fail.
/// </summary>
public static class TemplateParameterValidator
{
    // A parameter key: letter-led, then letters/digits/underscore/dash. Distinct from slot keys (which are
    // kebab-only) — params allow underscores so authors can mirror common config names like slack_channel.
    private static readonly Regex KeyPattern = new("^[a-zA-Z][a-zA-Z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Authoring-time check (export): the declarations must be well-formed. The key invariant — <c>Default</c>
    /// is mandatory when <c>Required == false</c> — is what makes the install-time value map total over every
    /// declared key, so a residual <c>{{param:…}}</c> token can only ever mean an authoring bug.
    /// </summary>
    public static IReadOnlyList<TemplateParameter> ValidateDeclarations(IReadOnlyList<TemplateParameter>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return [];
        }

        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in parameters)
        {
            var key = parameter.Key ?? string.Empty;
            if (!KeyPattern.IsMatch(key))
            {
                errors.Add($"Parameter key '{key}' is invalid — use a letter-led name of letters, digits, '_' or '-'.");
                continue;
            }

            if (!seen.Add(key))
            {
                errors.Add($"Duplicate parameter key '{key}'.");
            }

            if (!TemplateParameterTypes.IsKnown(parameter.Type))
            {
                errors.Add($"Parameter '{key}' has unknown type '{parameter.Type}'.");
            }

            if (parameter.Type == TemplateParameterTypes.Enum && (parameter.Options is null || parameter.Options.Count == 0))
            {
                errors.Add($"Enum parameter '{key}' must declare at least one option.");
            }

            // The invariant: optional ⇒ must carry a default, so the value map is total.
            if (!parameter.Required && string.IsNullOrEmpty(parameter.Default))
            {
                errors.Add($"Optional parameter '{key}' must declare a default value.");
            }

            // A declared default, when present, must itself be valid for the type.
            if (!string.IsNullOrEmpty(parameter.Default) && !TryCoerce(parameter, parameter.Default!, out _, out var defaultError))
            {
                errors.Add($"Default for parameter '{key}': {defaultError}");
            }
        }

        if (errors.Count > 0)
        {
            throw new TemplateParameterException(errors);
        }

        return parameters;
    }

    /// <summary>
    /// Install/insert-time check. Resolves each declared parameter to its effective raw value (supplied, else
    /// default), coerces it to the declared type, and returns the typed value map. Collects every error before
    /// throwing. Rejects values carrying a <c>slot:</c> or <c>{{param:</c> token so a substituted value can
    /// never inject another slot/parameter into the later credential-rebind pass.
    /// </summary>
    public static IReadOnlyDictionary<string, ParameterValue> Validate(
        IReadOnlyList<TemplateParameter> declared,
        IReadOnlyDictionary<string, string>? rawValues)
    {
        declared ??= [];
        rawValues ??= new Dictionary<string, string>(StringComparer.Ordinal);

        var errors = new List<string>();
        var declaredKeys = declared.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var suppliedKey in rawValues.Keys.Where(k => !declaredKeys.Contains(k)))
        {
            errors.Add($"Unknown parameter '{suppliedKey}'.");
        }

        var resolved = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var parameter in declared)
        {
            var supplied = rawValues.TryGetValue(parameter.Key, out var raw) ? raw : null;
            var effective = !string.IsNullOrWhiteSpace(supplied) ? supplied! : parameter.Default;

            if (string.IsNullOrWhiteSpace(effective))
            {
                if (parameter.Required)
                {
                    errors.Add($"Parameter '{parameter.Label}' ({parameter.Key}) is required.");
                }

                continue;
            }

            // Reject only values the rewriters would actually act on — a whole-value slot token (which the
            // credential-rebind pass that runs next would rebind) or a recognized {{param:key}} token. A bare
            // "slot:" or "{{" sitting inside a larger string (e.g. a URL path) is left alone.
            if (CredentialSlotModule.IsCredentialSlotToken(effective) || CredentialSlotModule.ContainsParameterToken(effective))
            {
                errors.Add($"Parameter '{parameter.Key}' value must not be a 'slot:' token or contain a '{{{{param:…}}}}' token.");
                continue;
            }

            if (!TryCoerce(parameter, effective, out var value, out var coerceError))
            {
                errors.Add($"Parameter '{parameter.Key}': {coerceError}");
                continue;
            }

            resolved[parameter.Key] = value!;
        }

        if (errors.Count > 0)
        {
            throw new TemplateParameterException(errors);
        }

        return resolved;
    }

    private static bool TryCoerce(TemplateParameter parameter, string raw, out ParameterValue? value, out string? error)
    {
        value = null;
        error = null;
        var text = raw.Trim();

        switch (parameter.Type)
        {
            case TemplateParameterTypes.Number:
                if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"'{raw}' is not a number.";
                    return false;
                }

                value = new ParameterValue(number, text);
                return true;

            case TemplateParameterTypes.Boolean:
                if (!bool.TryParse(text, out var flag))
                {
                    error = $"'{raw}' is not true/false.";
                    return false;
                }

                value = new ParameterValue(flag, flag ? "true" : "false");
                return true;

            case TemplateParameterTypes.Enum:
                if (parameter.Options is null || !parameter.Options.Contains(text, StringComparer.Ordinal))
                {
                    error = $"'{raw}' is not one of [{string.Join(", ", parameter.Options ?? [])}].";
                    return false;
                }

                value = new ParameterValue(text, text);
                return true;

            default: // string
                value = new ParameterValue(raw, raw);
                return true;
        }
    }
}
