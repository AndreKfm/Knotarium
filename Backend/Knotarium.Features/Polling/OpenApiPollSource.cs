using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>Polls an imported OpenAPI operation, reusing the interpreter via IOpenApiOperationInvoker.</summary>
public sealed class OpenApiPollSource : IPollSource
{
    private readonly IOpenApiOperationInvoker _invoker;

    public OpenApiPollSource(IOpenApiOperationInvoker invoker) =>
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));

    public string Kind => "openapi";

    public async Task<PollResult> PollAsync(PollContext context, CancellationToken cancellationToken)
    {
        using var configDoc = JsonDocument.Parse(context.ConfigJson);
        var root = configDoc.RootElement;

        var serverConfigId = GetString(root, "serverConfigId")
            ?? throw new InvalidOperationException("OpenAPI poll source is missing 'serverConfigId'.");
        var operationId = GetString(root, "operationId")
            ?? throw new InvalidOperationException("OpenAPI poll source is missing 'operationId'.");
        var specVersion = GetString(root, "specVersion");
        var strategy = PollStrategyParser.Parse(GetString(root, "changeDetection"));
        var jsonPath = GetString(root, "jsonCursorPath");

        var response = await _invoker.InvokeAsync(serverConfigId, operationId, specVersion, cancellationToken);

        return strategy switch
        {
            PollChangeDetection.Etag => PollValidator.FromValidator(response.ETag, context.Cursor, response.Body),
            PollChangeDetection.LastModified => PollValidator.FromValidator(response.LastModified, context.Cursor, response.Body),
            _ => BodyChangeDetector.Detect(strategy, response.Body, context.Cursor, jsonPath)
        };
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
