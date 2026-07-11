# Step 03 — Persistence (EF-Entities & Stores)

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 01](step-01.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §7 (Persistence), §11 (Entscheidung 1: EnsureCreated)

---

## Ziel

Neue EF-Core-Entities in `AppDbContext`, Store-Implementierungen für `IOpenApiSpecStore` und `IServerConfigStore` (aus Step 01). **Dev-DB-Datei muss nach diesem Schritt neu erstellt werden** (`EnsureCreated()` legt die neuen Tabellen nur bei frischer DB an).

---

## Neue Dateien

### `Knotarium.Infrastructure/Persistence/OpenApi/`

```
OpenApiSpecEntity.cs
OpenApiSpecVersionEntity.cs
ServerConfigEntity.cs
OpenApiSpecStore.cs
ServerConfigStore.cs
```

#### Entities

```csharp
// OpenApiSpecEntity.cs
public class OpenApiSpecEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public List<OpenApiSpecVersionEntity> Versions { get; set; } = new();
}

// OpenApiSpecVersionEntity.cs
public class OpenApiSpecVersionEntity
{
    public Guid RowId { get; set; }          // PK
    public string SpecId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }   // monoton, 1-basiert
    public string OriginalFormat { get; set; } = string.Empty;
    public string ParsedSpecJson { get; set; } = string.Empty;  // serialisierte ParsedSpec
    public DateTimeOffset ImportedAtUtc { get; set; }
    public OpenApiSpecEntity Spec { get; set; } = null!;
}

// ServerConfigEntity.cs
public class ServerConfigEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    // Refinement C: stored as JSON blob, e.g. {"environment":"prod","region":"eu"}
    public string ServerVariablesJson { get; set; } = "{}";
    public string SecuritySchemeType { get; set; } = string.Empty;
    public string? CredentialRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

#### AppDbContext-Erweiterung

In `AppDbContext` ergänzen:
```csharp
public DbSet<OpenApiSpecEntity> OpenApiSpecs { get; set; } = null!;
public DbSet<OpenApiSpecVersionEntity> OpenApiSpecVersions { get; set; } = null!;
public DbSet<ServerConfigEntity> ServerConfigs { get; set; } = null!;
```

`OnModelCreating` konfigurieren (analog bestehender Entities):
- `OpenApiSpecVersionEntity`: PK = `RowId`, FK = `SpecId`, Index auf `(SpecId, VersionNumber)` unique.
- `ParsedSpecJson` via bestehendem `JsonValueConverter`-Muster, oder als plain `string`-Spalte (JSON-Blob).

#### Stores

`OpenApiSpecStore` implementiert `IOpenApiSpecStore`:
- `SaveAsync`: Prüft ob `OpenApiSpecEntity` mit dieser Id existiert → anlegen oder aktualisieren; `VersionNumber` = max(existing)+1 oder 1; serialisiert `ParsedSpec` → JSON → `ParsedSpecJson`.
- `GetLatestAsync`: Lädt Entity + höchste Version → deserialisiert → `ParsedSpec`.
- `ListAsync`: Gibt `ImportedSpec` aus der jeweils höchsten Version zurück (kein Full-Blob).
- `GetVersionsAsync`: Alle Versionen zu einer Id als `ImportedSpec`-Liste.
- `GetOperationAsync`: Lädt die höchste Version, deserialisiert `ParsedSpec`, sucht Operation by `OperationId`.

`ServerConfigStore` implementiert `IServerConfigStore` — CRUD direkt auf `ServerConfigEntity`.

---

## DI-Registrierung

In `Knotarium.Infrastructure/DependencyInjection.cs` (oder wo bestehende Services registriert sind):
```csharp
services.AddScoped<IOpenApiSpecStore, OpenApiSpecStore>();
services.AddScoped<IServerConfigStore, ServerConfigStore>();
```

---

## Tests

**Projekt:** `Knotarium.Tests`  
**Datei:** `Knotarium.Tests/OpenApi/PersistenceTests.cs`

Verwendet SQLite In-Memory (analog `DatabaseWorkflowStoreTests.cs`).

### Testmatrix

| Test | Szenario | Erwartung |
|---|---|---|
| `SaveAsync_NewSpec_CreatesVersionOne` | Erste Import | `VersionNumber == 1` |
| `SaveAsync_SameSpecIdAgain_IncrementsVersion` | Zweiter Import gleiche Id | `VersionNumber == 2` |
| `GetLatestAsync_ReturnsHighestVersion` | 3 Versionen vorhanden | Version 3 zurück |
| `GetLatestAsync_UnknownId_ReturnsNull` | Unbekannte Id | `null` |
| `GetVersionAsync_KnownVersion_ReturnsThatVersion` | 3 Versionen, hol Version 2 | Version 2 zurück |
| `GetVersionAsync_UnknownVersion_ReturnsNull` | Version 99 existiert nicht | `null` |
| `ListAsync_ReturnsAllSpecs_OnlyLatestVersion` | 2 Specs, je 2 Versionen | 2 Einträge, je höchste Version |
| `GetVersionsAsync_ReturnAllVersions` | 3 Versionen | 3 Einträge aufsteigend |
| `GetOperationAsync_KnownId_ReturnsOperation` | Bekannte OperationId | Korrekte Operation |
| `GetOperationAsync_UnknownId_ReturnsNull` | Unbekannte OperationId | `null` |
| `ServerConfig_CreateAndGet_RoundTrip` | Create + Get | Alle Felder identisch |
| `ServerConfig_Update_ChangesFields` | Update Name | Neuer Name gespeichert |
| `ServerConfig_Delete_RemovesEntry` | Delete + Get | `null` |
| `ServerConfig_List_ReturnsAllEntries` | 3 Configs | 3 Einträge |

### Beispiel-Testcode

```csharp
public class PersistenceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly OpenApiSpecStore _specStore;
    private readonly ServerConfigStore _configStore;

    public PersistenceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _specStore = new OpenApiSpecStore(_db);
        _configStore = new ServerConfigStore(_db);
    }

    [Fact]
    public async Task SaveAsync_NewSpec_CreatesVersionOne()
    {
        var parsed = BuildMinimalParsedSpec("my-api");
        var saved = await _specStore.SaveAsync(parsed);
        Assert.Equal(1, saved.SpecVersionNumber);
    }

    [Fact]
    public async Task SaveAsync_SameSpecIdAgain_IncrementsVersion()
    {
        var parsed = BuildMinimalParsedSpec("my-api");
        await _specStore.SaveAsync(parsed);
        var second = await _specStore.SaveAsync(parsed);
        Assert.Equal(2, second.SpecVersionNumber);
    }

    public void Dispose() => _db.Dispose();

    private static ParsedSpec BuildMinimalParsedSpec(string id) => new(
        new ImportedSpec(new OpenApiSpecId(id), "My API", "1.0", "openapi3.0",
            ["https://example.com"], [], DateTimeOffset.UtcNow, 0),
        [new ApiOperation("listItems", "GET", "/items", null, [], [], null, [])],
        [], []);
}
```

---

## Definition of Done

- [ ] `dotnet build Knotarium.Infrastructure` ohne Fehler
- [ ] `dotnet build Knotarium.Tests` ohne Fehler
- [ ] Alle `PersistenceTests`-Tests grün
- [ ] Dev-DB neu erstellt, Anwendung startet ohne Fehler
- [ ] Keine Regressions
