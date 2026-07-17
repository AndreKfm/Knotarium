// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;

namespace Knotarium.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Minimal SemVer + constraint matching for package resolution. Scoped to bundles
// (no general-purpose dependency pulled in): just enough to compare versions and
// satisfy the manifest's VersionConstraintOrPin tokens — pins ("1.2.3"),
// comparators (">=1.0.0", "<2.0.0"), caret ("^1.2.3"), tilde ("~1.2.3"), and
// wildcard ("*"/"any"). Build metadata ("+…") is ignored; pre-release ordering
// follows SemVer §11.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A parsed semantic version. Comparison follows SemVer precedence (pre-release &lt; release).</summary>
public sealed record SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = new SemanticVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        // Drop build metadata, then split off the pre-release tag.
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0)
            {
                return false;
            }
        }

        var parts = s.Split('.');
        if (parts.Length is 0 or > 3)
        {
            return false;
        }

        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
            {
                return false;
            }
        }

        version = new SemanticVersion(nums[0], nums[1], nums[2], pre);
        return true;
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a valid semantic version.");

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    // SemVer §11: a version with a pre-release has *lower* precedence than the same core without one;
    // otherwise compare dot-separated identifiers (numeric numerically, else ASCII), shorter set lower.
    private static int ComparePreRelease(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;

        var left = a.Split('.');
        var right = b.Split('.');
        var shared = Math.Min(left.Length, right.Length);
        for (var i = 0; i < shared; i++)
        {
            var ln = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var li);
            var rn = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ri);
            int cmp;
            if (ln && rn) cmp = li.CompareTo(ri);
            else if (ln) cmp = -1;      // numeric identifiers are lower than alphanumeric
            else if (rn) cmp = 1;
            else cmp = string.CompareOrdinal(left[i], right[i]);
            if (cmp != 0) return cmp;
        }

        return left.Length.CompareTo(right.Length);
    }
}

/// <summary>A parsed version constraint that a candidate <see cref="SemanticVersion"/> either satisfies or not.</summary>
public sealed class VersionConstraint
{
    private readonly Func<SemanticVersion, bool> _predicate;
    private readonly string _text;

    private VersionConstraint(string text, Func<SemanticVersion, bool> predicate)
    {
        _text = text;
        _predicate = predicate;
    }

    public bool IsSatisfiedBy(SemanticVersion version) => _predicate(version);

    public override string ToString() => _text;

    public static VersionConstraint Parse(string? text)
    {
        var raw = text?.Trim() ?? string.Empty;
        if (raw.Length == 0 || raw is "*" or "any" or "latest" or "x")
        {
            return new VersionConstraint(raw.Length == 0 ? "*" : raw, _ => true);
        }

        if (raw[0] is '^')
        {
            return Caret(raw);
        }

        if (raw[0] is '~')
        {
            return Tilde(raw);
        }

        foreach (var op in new[] { ">=", "<=", "==", ">", "<", "=" })
        {
            if (raw.StartsWith(op, StringComparison.Ordinal))
            {
                var bound = SemanticVersion.Parse(raw[op.Length..]);
                return new VersionConstraint(raw, Comparator(op, bound));
            }
        }

        // Bare version => exact pin.
        var pin = SemanticVersion.Parse(raw);
        return new VersionConstraint(raw, v => v.CompareTo(pin) == 0);
    }

    private static Func<SemanticVersion, bool> Comparator(string op, SemanticVersion bound) => op switch
    {
        ">=" => v => v.CompareTo(bound) >= 0,
        "<=" => v => v.CompareTo(bound) <= 0,
        ">" => v => v.CompareTo(bound) > 0,
        "<" => v => v.CompareTo(bound) < 0,
        _ => v => v.CompareTo(bound) == 0, // "=" / "=="
    };

    // ^1.2.3 => >=1.2.3 <2.0.0; ^0.2.3 => >=0.2.3 <0.3.0; ^0.0.3 => >=0.0.3 <0.0.4 (npm semantics).
    private static VersionConstraint Caret(string raw)
    {
        var b = SemanticVersion.Parse(raw[1..]);
        var upper = b.Major > 0
            ? new SemanticVersion(b.Major + 1, 0, 0)
            : b.Minor > 0
                ? new SemanticVersion(0, b.Minor + 1, 0)
                : new SemanticVersion(0, 0, b.Patch + 1);
        return new VersionConstraint(raw, v => v.CompareTo(b) >= 0 && v.CompareTo(upper) < 0);
    }

    // ~1.2.3 => >=1.2.3 <1.3.0 (allows patch-level changes within the stated minor).
    private static VersionConstraint Tilde(string raw)
    {
        var b = SemanticVersion.Parse(raw[1..]);
        var upper = new SemanticVersion(b.Major, b.Minor + 1, 0);
        return new VersionConstraint(raw, v => v.CompareTo(b) >= 0 && v.CompareTo(upper) < 0);
    }
}
