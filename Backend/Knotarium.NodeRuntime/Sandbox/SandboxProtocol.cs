// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.NodeRuntime.Sandbox;

/// <summary>
/// Wire protocol between the host and a sandbox worker process. One duplex byte stream
/// (a named pipe) carries length-prefixed JSON messages in both directions; correlation
/// ids let host-callback round-trips (state, HTTP, secrets) interleave with the pending
/// execute call. Shared by the host runner (Knotarium.Features) and the worker executable
/// (Knotarium.SandboxWorker), which is why it lives here in NodeRuntime.
/// </summary>
public static class SandboxMessageTypes
{
    // host → worker
    public const string Execute = "execute";
    public const string Cancel = "cancel";
    public const string CallbackResult = "callbackResult";
    // worker → host
    public const string ExecuteResult = "executeResult";
    public const string Callback = "callback";
    public const string Log = "log"; // one-way, no response
}

public static class SandboxCallbackKinds
{
    public const string GetVariable = "getVariable";
    public const string SetVariable = "setVariable";
    public const string TryResolveVariable = "tryResolveVariable";
    public const string HttpSend = "httpSend";
    public const string GetSecret = "getSecret";
}

/// <summary>
/// The single envelope for every message. Modelled as one record with nullable fields
/// (rather than JSON polymorphism) to keep serialization trivial on both sides; the
/// <see cref="Type"/> discriminator says which fields are meaningful.
/// </summary>
public sealed record SandboxMessage
{
    public required string Type { get; init; }

    /// <summary>Execution id for execute/cancel/executeResult; correlation id for callback pairs.</summary>
    public string? Id { get; init; }

    // -- execute --
    public byte[]? AssemblyBytes { get; init; }
    public Dictionary<string, JsonElement>? Inputs { get; init; }
    public int TimeoutSeconds { get; init; }

    // -- executeResult --
    public string? OutputName { get; init; }
    public JsonElement? Payload { get; init; }
    /// <summary>Mirrors NodeExecutionStatus: Succeeded | Failed | Cancelled.</summary>
    public string? Status { get; init; }
    public string? Error { get; init; }

    // -- callback / callbackResult --
    public string? CallbackKind { get; init; }
    public string? Name { get; init; }
    public JsonElement? Value { get; init; }
    public bool Found { get; init; }
    public SandboxHttpRequest? HttpRequest { get; init; }
    public SandboxHttpResponse? HttpResponse { get; init; }

    // -- log --
    public string? LogLevel { get; init; }
    public string? LogMessage { get; init; }
}

/// <summary>HttpRequestMessage flattened for the wire. Bodies are fully buffered — streaming is unsupported in the sandbox.</summary>
public sealed record SandboxHttpRequest
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    public List<KeyValuePair<string, string[]>>? Headers { get; init; }
    public byte[]? ContentBytes { get; init; }
    public List<KeyValuePair<string, string[]>>? ContentHeaders { get; init; }
}

public sealed record SandboxHttpResponse
{
    public int StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public List<KeyValuePair<string, string[]>>? Headers { get; init; }
    public byte[]? ContentBytes { get; init; }
    public List<KeyValuePair<string, string[]>>? ContentHeaders { get; init; }
}

/// <summary>
/// Length-prefixed JSON framing over a duplex stream. Writes must be serialized by the
/// caller (both sides use a single writer loop / write lock); reads are single-consumer.
/// </summary>
public static class SandboxFraming
{
    /// <summary>Hard cap per frame. Generous (assemblies + buffered HTTP bodies cross this pipe) but bounded so a corrupt length prefix cannot allocate unbounded memory.</summary>
    public const int MaxFrameBytes = 128 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync(Stream stream, SandboxMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"Sandbox frame of {payload.Length} bytes exceeds the {MaxFrameBytes}-byte limit.");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; returns null on clean end-of-stream (peer closed between frames).</summary>
    public static async Task<SandboxMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        if (!await TryReadExactAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid sandbox frame length {length}.");
        }

        var payload = new byte[length];
        if (!await TryReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Sandbox stream closed mid-frame.");
        }

        return JsonSerializer.Deserialize<SandboxMessage>(payload, Options)
            ?? throw new InvalidDataException("Sandbox frame deserialized to null.");
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException("Sandbox stream closed mid-frame.");
            }
            offset += read;
        }
        return true;
    }
}
