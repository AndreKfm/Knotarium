# Step 01 — Core Contracts & Normalized Model

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §3, §4 (Datenmodell), §6 (ServerConfig-Shape)

---

## Ziel

Alle stabilen Contracts und Domain-Records, auf die sich `Features`, `Infrastructure` und `Api` stützen, leben in `Knotarium.Core`. Kein Code aus anderen Modulen ist nach diesem Schritt nötig — alles hier lässt sich isoliert kompilieren und testen.

---

## Neue Dateien

### `Knotarium.Core/Domain/OpenApi/`

```
ImportedSpec.cs
ApiOperation.cs
ApiParameter.cs
ApiRequestBody.cs
ApiSchema.cs
SecurityScheme.cs
ServerConfig.cs          ← Domain-Record (nicht EF-Entity)
OpenApiSpecId.cs         ← Typed-ID-Wrapper analog NodePackageId
```

#### Records (alle `sealed record`)

```csharp
// ImportedSpec.cs
public sealed record ImportedSpec(
    OpenApiSpecId Id,
    string Title,
    string Version,
    string OriginalFormat,        // "swagger2.0" | "openapi3.0" | "openapi3.1"
    IReadOnlyList<string> DefaultServers,
    IReadOnlyList<string> Tags,
    DateTimeOffset ImportedAtUtc,
    int SpecVersionNumber
);

// ApiOperation.cs
public sealed record ApiOperation(
    string OperationId,
    string Method,
    string PathTemplate,
    string? Summary,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ApiParameter> Parameters,
    ApiRequestBody? RequestBody,
    IReadOnlyList<string> SecurityRefs
);

// ApiParameter.cs
public sealed record ApiParameter(
    string Name,
    string In,           // "path" | "query" | "header" | "cookie"
    bool Required,
    string? Description,
    string SchemaJson    // raw JSON-Schema als string
);

// ApiRequestBody.cs
public sealed record ApiRequestBody(
    bool Required,
    IReadOnlyList<string> MediaTypes,
    string SchemaJson
);

// ApiSchema.cs
public sealed record ApiSchema(
    string Name,
    string? Description,
    string SchemaJson
);

// SecurityScheme.cs
public sealed record SecurityScheme(
    string Name,
    string Type,         // "apiKey" | "http" | "oauth2"
    string? Scheme,      // "bearer" | "basic"
    string? In,          // "header" | "query" (apiKey)
    string? ParamName,
    string? TokenUrl     // OAuth2 client-credentials
);

// ServerConfig.cs  (Domain-Record, kein EF)
public sealed record ServerConfigInfo(
    string Id,
    string Name,
    string BaseUrl,
    IReadOnlyDictionary<string, string> ServerVariables,  // Refinement C: URL-Template-Substitution
    string SecuritySchemeType,   // "none" | "apiKey" | "http_bearer" | "http_basic" | "oauth2"
    string? CredentialRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
```

#### Typed ID

```csharp
// OpenApiSpecId.cs  — analog NodePackageId
public readonly record struct OpenApiSpecId(string Value)
{
    public override string ToString() => Value;
}
```

### `Knotarium.Core/Contracts/OpenApi/`

```
IOpenApiParser.cs
IOpenApiSpecStore.cs
IServerConfigStore.cs
ParsedSpec.cs
```

```csharp
// ParsedSpec.cs — Ausgabe des Parsers
public sealed record ParsedSpec(
    ImportedSpec Metadata,
    IReadOnlyList<ApiOperation> Operations,
    IReadOnlyList<ApiSchema> Schemas,
    IReadOnlyList<SecurityScheme> SecuritySchemes
);

// IOpenApiParser.cs
public interface IOpenApiParser
{
    /// <summary>Parses JSON or YAML bytes into a normalized ParsedSpec.</summary>
    /// <exception cref="OpenApiParseException">On parse error or external $ref detected.</exception>
    Task<ParsedSpec> ParseAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default);
}

// IOpenApiSpecStore.cs
public interface IOpenApiSpecStore
{
    Task<ImportedSpec> SaveAsync(ParsedSpec spec, CancellationToken ct = default);
    Task<(ImportedSpec Spec, ParsedSpec Full)?> GetLatestAsync(OpenApiSpecId id, CancellationToken ct = default);
    // Refinement B: pinned version lookup (used by executor when specVersion is set)
    Task<(ImportedSpec Spec, ParsedSpec Full)?> GetVersionAsync(OpenApiSpecId id, int versionNumber, CancellationToken ct = default);
    Task<IReadOnlyList<ImportedSpec>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ImportedSpec>> GetVersionsAsync(OpenApiSpecId id, CancellationToken ct = default);
    Task<ApiOperation?> GetOperationAsync(OpenApiSpecId id, string operationId, CancellationToken ct = default);
}

// IServerConfigStore.cs
public interface IServerConfigStore
{
    Task<ServerConfigInfo> CreateAsync(ServerConfigInfo config, CancellationToken ct = default);
    Task<ServerConfigInfo> UpdateAsync(ServerConfigInfo config, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<ServerConfigInfo?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ServerConfigInfo>> ListAsync(CancellationToken ct = default);
}
```

### `Knotarium.Core/Exceptions/OpenApiParseException.cs`

```csharp
public sealed class OpenApiParseException : Exception
{
    public OpenApiParseException(string message) : base(message) { }
    public OpenApiParseException(string message, Exception inner) : base(message, inner) { }
}
```

---

## Abhängigkeiten

- Nur `Knotarium.Core` — keine externen NuGet-Pakete nötig.
- Keine Referenz auf `Infrastructure`, `Features` oder `Api`.

---

## Tests

**Projekt:** `Knotarium.Tests` (bestehend)  
**Datei:** `Knotarium.Tests/OpenApi/CoreModelTests.cs`

### Was wird getestet?

| Test | Szenario | Erwartung |
|---|---|---|
| `OpenApiSpecId_ToString_ReturnsValue` | `new OpenApiSpecId("x").ToString()` | `"x"` |
| `OpenApiSpecId_Equality_SameValue` | Zwei IDs mit gleichem Value | Gleich (`==`) |
| `OpenApiSpecId_Equality_DifferentValue` | Zwei IDs mit unterschiedlichem Value | Ungleich |
| `ImportedSpec_Record_Immutable` | `with`-Ausdruck ändert nur ein Feld | Alle anderen Felder gleich |
| `ApiParameter_In_Values_AreExpected` | Bekannte `In`-Werte | Entsprechen Spezifikation |
| `ParsedSpec_Operations_PreservesOrder` | Liste mit 3 Operationen | Reihenfolge erhalten |

### Beispiel-Testcode

```csharp
public class CoreModelTests
{
    [Fact]
    public void OpenApiSpecId_ToString_ReturnsValue()
    {
        var id = new OpenApiSpecId("petstore");
        Assert.Equal("petstore", id.ToString());
    }

    [Fact]
    public void OpenApiSpecId_Equality_SameValue()
    {
        var a = new OpenApiSpecId("x");
        var b = new OpenApiSpecId("x");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ImportedSpec_With_ChangesOnlyTargetField()
    {
        var original = new ImportedSpec(
            new OpenApiSpecId("id"), "Title", "1.0", "openapi3.0",
            [], [], DateTimeOffset.UtcNow, 1);
        var modified = original with { Title = "New" };
        Assert.Equal("New", modified.Title);
        Assert.Equal(original.Id, modified.Id);
        Assert.Equal(original.SpecVersionNumber, modified.SpecVersionNumber);
    }
}
```

---

## Definition of Done

- [ ] `dotnet build Knotarium.Core` ohne Fehler/Warnings
- [ ] `dotnet build Knotarium.Tests` ohne Fehler (Tests kompilieren)
- [ ] Alle Tests in `CoreModelTests.cs` grün
- [ ] Keine anderen Tests gebrochen
