using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;
using KnotGarden.Core.Domain.OpenApi;

namespace KnotGarden.Features.OpenApi;

public sealed class ImportOpenApiSpecHandler(
    IOpenApiParser parser,
    IOpenApiSpecStore store,
    OpenApiNodeGenerator generator,
    INodePackageStore packageStore)
{
    public async Task<ImportedSpec> HandleAsync(
        ReadOnlyMemory<byte> rawContent,
        string? specIdOverride = null,
        CancellationToken ct = default)
    {
        var parsed = await parser.ParseAsync(rawContent, ct);

        // Identity defaults to the title slug (re-imports of the same title increment version
        // history). An explicit id lets callers keep two distinct APIs that share a title from
        // silently merging into one another's version history.
        var slug = SlugifyId(specIdOverride);
        if (slug is not null)
        {
            parsed = parsed with { Metadata = parsed.Metadata with { Id = new OpenApiSpecId(slug) } };
        }

        var saved = await store.SaveAsync(parsed, ct);

        // Generate and persist the node package so the operation appears in the palette.
        // Use the version number assigned by SaveAsync (parsed.Metadata still carries 0).
        await PersistNodePackageAsync(parsed, saved.SpecVersionNumber, ct);

        return saved;
    }

    private async Task PersistNodePackageAsync(Core.Contracts.OpenApi.ParsedSpec parsed, int specVersionNumber, CancellationToken ct)
    {
        var pkg = generator.Generate(parsed);
        var packageId = NodePackageId.Create(pkg.PackageId);
        var manifestJson = generator.GenerateManifestJson(parsed);

        var version = new NodePackageVersion
        {
            Id = NodePackageVersionId.New(),
            NodePackageId = packageId,
            Version = $"1.0.{specVersionNumber}",
            ManifestJson = manifestJson,
            // Option C: openapi.* packages carry no executor source — they run through the
            // pre-compiled OpenApiInterpreterExecutor (manifest tier: Interpreted).
            Source = string.Empty,
            Signature = null,
            Capabilities = new List<string> { "http", "credentials" },
            CreatedAt = DateTimeOffset.UtcNow
        };

        await packageStore.UpsertGeneratedPackageAsync(
            packageId, parsed.Metadata.Title, "Integrations", version, ct);
    }

    /// <summary>
    /// Slugifies a caller-supplied spec id with the same rule the parser uses for titles,
    /// returning null when the input is blank or reduces to nothing (so the title slug stands).
    /// </summary>
    private static string? SlugifyId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var slug = System.Text.RegularExpressions.Regex
            .Replace(raw.ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
        return string.IsNullOrEmpty(slug) ? null : slug;
    }
}
