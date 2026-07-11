# Step 05 — Server-Configurations API

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 03](step-03.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §6 (Server Configuration), §2 (Credential-Reuse)

---

## Ziel

CRUD-API für Server-Konfigurationen. Eine `ServerConfig` speichert nur einen `CredentialRef` (Zeiger auf eine Zeile in der `Credentials`-Tabelle) — **nie** die Secret-Werte selbst. Die Verschlüsselung läuft über den bestehenden `AesCredentialCipher`-Pfad.

---

## Api-Endpoints

Analog `/api/credentials` (`Program.cs:1071–1148`):

```
GET    /api/server-configs                → IServerConfigStore.ListAsync
POST   /api/server-configs                → IServerConfigStore.CreateAsync
GET    /api/server-configs/{id}           → IServerConfigStore.GetAsync  (404 wenn nicht vorhanden)
PUT    /api/server-configs/{id}           → IServerConfigStore.UpdateAsync
DELETE /api/server-configs/{id}           → IServerConfigStore.DeleteAsync
```

**Request-DTOs:**
```csharp
record CreateServerConfigRequest(
    string Name,
    string BaseUrl,
    // Refinement C: key/value pairs substituted into BaseUrl templates, e.g. {environment} → "prod"
    Dictionary<string, string>? ServerVariables,
    string SecuritySchemeType,   // "none" | "apiKey" | "http_bearer" | "http_basic" | "oauth2"
    string? CredentialRef        // Id einer existierenden Credential
);

record UpdateServerConfigRequest(
    string Name,
    string BaseUrl,
    Dictionary<string, string>? ServerVariables,
    string SecuritySchemeType,
    string? CredentialRef
);
```

**Validation:**
- `Name` und `BaseUrl` sind Pflichtfelder → 400 wenn leer.
- `CredentialRef`, wenn angegeben: muss existierende Credential-Id sein → 400 wenn nicht gefunden.

---

## Tests

**Projekt:** `Knotarium.Tests`  
**Datei:** `Knotarium.Tests/OpenApi/ServerConfigApiTests.cs`

Nutzt `WebApplicationFactory<Program>` mit In-Memory-SQLite.

### Testmatrix

| Test | HTTP-Aufruf | Erwartung |
|---|---|---|
| `Create_ValidConfig_Returns201WithId` | POST | 201, `Id` nicht leer |
| `Create_MissingName_Returns400` | POST ohne Name | 400 |
| `Create_MissingBaseUrl_Returns400` | POST ohne BaseUrl | 400 |
| `Create_InvalidCredentialRef_Returns400` | POST mit unbekanntem CredentialRef | 400 |
| `Get_ExistingConfig_Returns200` | POST + GET | 200, Felder identisch |
| `Get_UnknownId_Returns404` | GET /server-configs/unknown | 404 |
| `List_AfterTwoCreates_ReturnsBoth` | 2× POST + GET /server-configs | 2 Einträge |
| `Update_ExistingConfig_ChangesName` | POST + PUT + GET | neuer Name |
| `Update_UnknownId_Returns404` | PUT /server-configs/unknown | 404 |
| `Delete_ExistingConfig_Returns204` | POST + DELETE | 204 |
| `Delete_UnknownId_Returns404` | DELETE /server-configs/unknown | 404 |
| `Create_WithValidCredentialRef_Returns201` | Erst Credential anlegen, dann Config | 201 |

---

## Definition of Done

- [ ] `dotnet build` (Solution) ohne Fehler
- [ ] Alle `ServerConfigApiTests`-Tests grün
- [ ] `CredentialRef` wird nie als Plaintext gespeichert (nur Id-Zeiger)
- [ ] Keine Regressions
