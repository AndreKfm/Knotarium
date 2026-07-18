// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.NodeRuntime.Sandbox;

namespace Knotarium.Features.Nodes.Sandbox;

/// <summary>
/// The host half of the Model-1 credential contract: sandboxed code only ever sees
/// <c>{{knotarium-secret:ref}}</c> placeholders; just before a proxied HTTP request leaves the
/// host, this class replaces them with the real secret in the URL, header values and — when the
/// body is valid UTF-8 — the body. A fabricated placeholder grants nothing beyond what
/// <c>GetSecretAsync</c> already granted (the ability to <i>use</i> a resolvable credential);
/// the plaintext itself never crosses into the worker process.
/// </summary>
public static class SandboxCredentialSubstitutor
{
    public const string PlaceholderPrefix = "{{knotarium-secret:";
    public const string PlaceholderSuffix = "}}";

    private static readonly Regex Placeholder = new(
        @"\{\{knotarium-secret:([^}]+)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string MakePlaceholder(string credentialRef)
        => PlaceholderPrefix + credentialRef + PlaceholderSuffix;

    /// <summary>Rewrites a wire-level request in place-ish (returns a copy when anything changed).</summary>
    public static async Task<SandboxHttpRequest> SubstituteAsync(
        SandboxHttpRequest request, ICredentialAccessor credentials, CancellationToken cancellationToken)
    {
        var url = await SubstituteStringAsync(request.Url, credentials, cancellationToken).ConfigureAwait(false);

        var headers = request.Headers;
        if (headers is not null)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                var values = headers[i].Value;
                for (var j = 0; j < values.Length; j++)
                {
                    values[j] = await SubstituteStringAsync(values[j], credentials, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var body = request.ContentBytes;
        if (body is not null && ContainsPlaceholderBytes(body))
        {
            // Substitute only when the body round-trips through UTF-8 losslessly; anything else
            // (binary payloads) is left untouched — a placeholder cannot meaningfully live there.
            var text = Encoding.UTF8.GetString(body);
            if (Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(body))
            {
                var substituted = await SubstituteStringAsync(text, credentials, cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(substituted, text))
                {
                    body = Encoding.UTF8.GetBytes(substituted);
                }
            }
        }

        return request with { Url = url, ContentBytes = body };
    }

    private static async Task<string> SubstituteStringAsync(
        string value, ICredentialAccessor credentials, CancellationToken cancellationToken)
    {
        if (!value.Contains(PlaceholderPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        var last = 0;
        foreach (Match match in Placeholder.Matches(value))
        {
            result.Append(value, last, match.Index - last);
            var secret = await credentials.GetSecretAsync(match.Groups[1].Value, cancellationToken).ConfigureAwait(false);
            // An unresolvable ref keeps its placeholder — the remote call then fails visibly
            // instead of silently sending an empty credential.
            result.Append(secret ?? match.Value);
            last = match.Index + match.Length;
        }
        result.Append(value, last, value.Length - last);
        return result.ToString();
    }

    private static bool ContainsPlaceholderBytes(byte[] body)
        => body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(PlaceholderPrefix)) >= 0;
}
