// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.NodeRuntime.Sandbox;

/// <summary>
/// Converts between <see cref="HttpRequestMessage"/>/<see cref="HttpResponseMessage"/> and
/// their wire DTOs. Bodies are fully buffered — the sandbox pipe does not stream. Used by
/// the worker (request → wire, wire → response) and the host (the reverse).
/// </summary>
public static class SandboxHttpTranslator
{
    public static async Task<SandboxHttpRequest> ToWireAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? content = null;
        List<KeyValuePair<string, string[]>>? contentHeaders = null;
        if (request.Content is not null)
        {
            content = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            contentHeaders = request.Content.Headers
                .Select(h => new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray())).ToList();
        }

        return new SandboxHttpRequest
        {
            Method = request.Method.Method,
            Url = request.RequestUri?.ToString() ?? throw new InvalidOperationException("Request has no URI."),
            Headers = request.Headers.Select(h => new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray())).ToList(),
            ContentBytes = content,
            ContentHeaders = contentHeaders
        };
    }

    public static HttpRequestMessage FromWire(SandboxHttpRequest wire)
    {
        var request = new HttpRequestMessage(new HttpMethod(wire.Method), wire.Url);
        if (wire.ContentBytes is not null)
        {
            request.Content = new ByteArrayContent(wire.ContentBytes);
            ApplyHeaders(wire.ContentHeaders, (k, v) => request.Content.Headers.TryAddWithoutValidation(k, v));
        }
        ApplyHeaders(wire.Headers, (k, v) => request.Headers.TryAddWithoutValidation(k, v));
        return request;
    }

    public static async Task<SandboxHttpResponse> ToWireAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new SandboxHttpResponse
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Headers = response.Headers.Select(h => new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray())).ToList(),
            ContentBytes = content,
            ContentHeaders = response.Content.Headers
                .Select(h => new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray())).ToList()
        };
    }

    public static HttpResponseMessage FromWire(SandboxHttpResponse wire)
    {
        var response = new HttpResponseMessage((HttpStatusCode)wire.StatusCode)
        {
            ReasonPhrase = wire.ReasonPhrase,
            Content = new ByteArrayContent(wire.ContentBytes ?? Array.Empty<byte>())
        };
        ApplyHeaders(wire.ContentHeaders, (k, v) => response.Content.Headers.TryAddWithoutValidation(k, v));
        ApplyHeaders(wire.Headers, (k, v) => response.Headers.TryAddWithoutValidation(k, v));
        return response;
    }

    private static void ApplyHeaders(
        List<KeyValuePair<string, string[]>>? headers, Func<string, IEnumerable<string>, bool> add)
    {
        if (headers is null)
        {
            return;
        }
        foreach (var header in headers)
        {
            add(header.Key, header.Value);
        }
    }
}
