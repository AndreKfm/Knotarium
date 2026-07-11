# Step 04 — Import- & List-API

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 02](step-02.md) und [Step 03](step-03.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §4 (Import-Flow, Grouping & Listing)

---

## Ziel

HTTP-Endpoints zum Importieren und Abfragen von Specs. Der Use-Case-Handler liegt in `Features`, die Endpoints in `Api` (nur Transport/DI). Kein Business-Logik in `Api`.

---

## Features: `Knotarium.Features/OpenApi/`

### `ImportOpenApiSpecHandler.cs`

```csharp
public sealed class ImportOpenApiSpecHandler(
    IOpenApiParser parser,
    IOpenApiSpecStore store)
{
    public async Task<ImportedSpec> HandleAsync(
        ReadOnlyMemory<byte> rawContent,
        CancellationToken ct = default)
    {
        var parsed = await parser.ParseAsync(rawContent, ct);
        return await store.SaveAsync(parsed, ct);
    }
}
```

### Grouping-Helper: `OpenApiGrouper.cs`

```csharp
public static class OpenApiGrouper
{
    /// Groups operations by primary tag; untagged → first path segment.
    public static IReadOnlyList<OperationGroup> Group(IReadOnlyList<ApiOperation> operations);
}

public sealed record OperationGroup(string Tag, IReadOnlyList<ApiOperation> Operations);
```

---

## Api: neue Endpoints in `Program.cs` (oder separates `OpenApiEndpoints.cs`)

Analog `Program.cs:1331` (Node-Package-Install-Endpoint):

```
POST   /api/openapi/specs                   → ImportOpenApiSpecHandler.HandleAsync
GET    /api/openapi/specs                   → IOpenApiSpecStore.ListAsync (grouped summary)
GET    /api/openapi/specs/{id}              → IOpenApiSpecStore.GetLatestAsync → grouped model
GET    /api/openapi/specs/{id}/versions     → IOpenApiSpecStore.GetVersionsAsync
GET    /api/openapi/specs/{id}/operations/{operationId}  → IOpenApiSpecStore.GetOperationAsync
```

**POST-Body:** Multipart-Form-Data (`file`) **oder** JSON `{ "content": "<raw spec text>" }`.

**Response-DTOs** (neue Records im Api-Projekt oder in Core als shared DTOs):
```csharp
record ImportSpecResponse(string Id, int VersionNumber, string Title,
    IReadOnlyList<OperationGroup> Groups, IReadOnlyList<ApiSchema> Schemas);

record SpecSummaryResponse(string Id, string Title, string ApiVersion,
    int LatestVersionNumber, DateTimeOffset ImportedAtUtc);
```

---

## Tests

**Projekt:** `Knotarium.Tests`  
**Datei:** `Knotarium.Tests/OpenApi/OpenApiApiTests.cs`

Nutzt `WebApplicationFactory<Program>` analog `WorkflowApiTests.cs`. In-Memory-SQLite, isolierte DB pro Test.

### Testmatrix

| Test | HTTP-Aufruf | Erwartung |
|---|---|---|
| `Import_ValidOpenApi30Json_Returns200WithId` | POST mit Petstore-3.0-JSON | 200, `Id` nicht leer, `VersionNumber == 1` |
| `Import_ValidSwagger20Yaml_Returns200` | POST mit Petstore-2.0-YAML | 200, `OriginalFormat == "swagger2.0"` |
| `Import_ExternalRef_Returns400` | POST mit external-ref.yaml | 400, Body enthält Fehlermeldung |
| `Import_InvalidContent_Returns400` | POST mit `"garbage"` | 400 |
| `Import_SameSpecTwice_VersionIncrements` | POST zweimal | 2. Response `VersionNumber == 2` |
| `List_AfterTwoImports_ReturnsBoth` | 2× POST, dann GET /specs | 200, 2 Einträge |
| `GetById_ExistingSpec_ReturnsGroupedModel` | POST + GET /specs/{id} | 200, `Groups` nicht leer |
| `GetById_UnknownId_Returns404` | GET /specs/unknown | 404 |
| `GetVersions_AfterTwoImports_ReturnsBoth` | 2× POST + GET /specs/{id}/versions | 2 Versionen |
| `GetOperation_KnownOperationId_Returns200` | POST + GET /specs/{id}/operations/{opId} | 200, korrekte Operation |
| `GetOperation_UnknownOperationId_Returns404` | GET /specs/{id}/operations/unknown | 404 |

### Fixture-Zugriff

Gleiche eingebettete Ressourcen wie in Step 02.

---

## Definition of Done

- [ ] `dotnet build` (Solution) ohne Fehler
- [ ] Alle `OpenApiApiTests`-Tests grün
- [ ] `ImportOpenApiSpecHandler` liegt in `Features`, kein Business-Logik in `Api`
- [ ] Keine Regressions
