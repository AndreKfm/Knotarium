# Step 02 — Parser-Adapter (Infrastructure)

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 01](step-01.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §3 (Normalisierungsschicht), §11 (Entscheidungen 2 + 4)

---

## Ziel

`Microsoft.OpenApi` wird als NuGet-Paket in `Knotarium.Infrastructure` hinzugefügt und dort hinter dem `IOpenApiParser`-Contract aus Step 01 gekapselt. `Features` und `Core` haben keine direkte Abhängigkeit auf `Microsoft.OpenApi`.

Der Adapter muss:
- Swagger 2.0 (JSON + YAML) → OpenAPI 3.x Intern-Modell up-converten
- OpenAPI 3.0 und 3.1 (JSON + YAML) direkt normalisieren
- Interne `$ref`s auflösen
- Externe `$ref`s / Multi-File-Specs **ablehnen** mit `OpenApiParseException`
- Tags-basiertes Grouping **nicht** übernehmen (wird in Features/UI gemacht); Tags nur übertragen

---

## NuGet

```xml
<!-- Knotarium.Infrastructure/Knotarium.Infrastructure.csproj -->
<PackageReference Include="Microsoft.OpenApi" Version="1.*" />
<PackageReference Include="Microsoft.OpenApi.Readers" Version="1.*" />
```

> Versionen nach aktuellem Stand prüfen; Mind. 1.6.x für 3.1-Support.

---

## Neue Dateien

### `Knotarium.Infrastructure/OpenApi/MicrosoftOpenApiParser.cs`

Implementiert `IOpenApiParser` aus `Knotarium.Core.Contracts.OpenApi`.

Kernlogik:

```
ReadOnlyMemory<byte> content
  → Detect YAML/JSON (starts with '{' / '[' → JSON, else YAML)
  → OpenApiReaderSettings { ReferenceResolution = ReferenceResolutionSetting.ResolveLocalReferences }
  → new OpenApiStringReader().Read(...)
  → Diagnostic errors? → throw OpenApiParseException
  → External $ref detected? → throw OpenApiParseException("External $ref not supported in v1: {ref}")
  → openApiDocument.Info.Extensions["x-swagger-version"] == "2.0"? → already up-converted by reader
  → Map to ParsedSpec (normalization below)
```

**Swagger 2.0 Up-Convert** — `Microsoft.OpenApi.Readers` konvertiert Swagger 2.0 intern in ein `OpenApiDocument` (3.x-Modell). Es müssen nur die Felder gemappt werden, die der Reader nicht automatisch überträgt:
- `host` + `basePath` + `schemes` → `servers[0].Url`
- `securityDefinitions` → bereits als `components.securitySchemes` vorhanden nach Reader-Konvertierung

**External-$ref-Erkennung:** Nach dem Lesen alle `$ref`-Werte traversieren; jede `$ref`, die nicht mit `#` beginnt → Exception.

**Normalisierung → `ParsedSpec`:**

| OpenAPI-Feld | → Core-Record |
|---|---|
| `info.title` | `ImportedSpec.Title` |
| `info.version` | `ImportedSpec.Version` |
| `servers[].url` | `ImportedSpec.DefaultServers` |
| alle Tags aus allen Operationen | `ImportedSpec.Tags` (distinct, sorted) |
| pro `paths[path][method]` | `ApiOperation` |
| `parameters[]` | `ApiParameter` (In normalisieren zu lowercase) |
| `requestBody` | `ApiRequestBody` |
| `components.schemas` | `ApiSchema[]` (JsonSchema als serialisierter JSON-String) |
| `components.securitySchemes` | `SecurityScheme[]` |

`OriginalFormat` aus Reader-Diagnostic ermitteln (`SpecificationVersion`).

---

## Tests

**Projekt:** `Knotarium.Tests`  
**Datei:** `Knotarium.Tests/OpenApi/ParserAdapterTests.cs`

**Test-Fixtures** (committen als eingebettete Ressourcen unter `Knotarium.Tests/OpenApi/Fixtures/`):
- `petstore-swagger20.json` — Swagger 2.0 JSON (Petstore)
- `petstore-swagger20.yaml` — Swagger 2.0 YAML
- `petstore-openapi30.json` — OpenAPI 3.0 JSON
- `petstore-openapi30.yaml` — OpenAPI 3.0 YAML
- `petstore-openapi31.json` — OpenAPI 3.1 JSON
- `minimal-no-tags.yaml` — Spec ohne Tags, ohne operationId auf manchen Operationen
- `external-ref.yaml` — Spec mit `$ref: './other.yaml#/components/...'`
- `internal-ref.yaml` — Spec mit internem `$ref: '#/components/schemas/Pet'`

### Testmatrix

| Test | Fixture | Erwartung |
|---|---|---|
| `Parse_Swagger20Json_ReturnsOperations` | petstore-swagger20.json | OriginalFormat="swagger2.0", ≥1 Operation |
| `Parse_Swagger20Yaml_ReturnsOperations` | petstore-swagger20.yaml | gleich wie JSON |
| `Parse_OpenApi30Json_ReturnsOperations` | petstore-openapi30.json | OriginalFormat="openapi3.0" |
| `Parse_OpenApi30Yaml_ReturnsOperations` | petstore-openapi30.yaml | gleich |
| `Parse_OpenApi31Json_ReturnsOperations` | petstore-openapi31.json | OriginalFormat="openapi3.1" |
| `Parse_ExternalRef_ThrowsOpenApiParseException` | external-ref.yaml | `OpenApiParseException` mit "External $ref" im Message |
| `Parse_InternalRef_Succeeds` | internal-ref.yaml | Kein Fehler, Schema korrekt aufgelöst |
| `Parse_NoTags_OperationsHaveEmptyTagList` | minimal-no-tags.yaml | `Tags == []` pro Operation |
| `Parse_Swagger20_ServersNormalized` | petstore-swagger20.json | `DefaultServers` enthält eine URL aus host+basePath |
| `Parse_InvalidContent_ThrowsOpenApiParseException` | `"not yaml or json"` | `OpenApiParseException` |
| `Parse_EmptyOperationId_GeneratesFallback` | minimal-no-tags.yaml | `OperationId` nicht null/leer |

### Beispiel-Testcode

```csharp
public class ParserAdapterTests
{
    private static readonly MicrosoftOpenApiParser Parser = new();

    private static ReadOnlyMemory<byte> LoadFixture(string name)
    {
        var asm = typeof(ParserAdapterTests).Assembly;
        var resourceName = $"Knotarium.Tests.OpenApi.Fixtures.{name}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture '{name}' not found.");
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("petstore-swagger20.json", "swagger2.0")]
    [InlineData("petstore-openapi30.json", "openapi3.0")]
    [InlineData("petstore-openapi31.json", "openapi3.1")]
    public async Task Parse_KnownFormat_ReturnsCorrectOriginalFormat(string fixture, string expectedFormat)
    {
        var result = await Parser.ParseAsync(LoadFixture(fixture));
        Assert.Equal(expectedFormat, result.Metadata.OriginalFormat);
    }

    [Fact]
    public async Task Parse_ExternalRef_ThrowsOpenApiParseException()
    {
        var content = LoadFixture("external-ref.yaml");
        var ex = await Assert.ThrowsAsync<OpenApiParseException>(() =>
            Parser.ParseAsync(content).AsTask());
        Assert.Contains("External $ref", ex.Message);
    }

    [Fact]
    public async Task Parse_Swagger20_ServersContainsUrl()
    {
        var result = await Parser.ParseAsync(LoadFixture("petstore-swagger20.json"));
        Assert.NotEmpty(result.Metadata.DefaultServers);
        Assert.All(result.Metadata.DefaultServers, s => Assert.False(string.IsNullOrEmpty(s)));
    }
}
```

---

## Definition of Done

- [ ] `Knotarium.Infrastructure` kompiliert (`dotnet build`)
- [ ] `Knotarium.Tests` kompiliert
- [ ] Alle `ParserAdapterTests`-Tests grün
- [ ] Kein direkter `Microsoft.OpenApi`-Import in `Core` oder `Features`
- [ ] Keine Regressions
