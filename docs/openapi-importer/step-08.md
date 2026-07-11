# Step 08 — OAuth2 Client-Credentials + Token-Cache

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 07](step-07.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §6 (Auth-Tabelle: OAuth2 client-credentials), §11 (Entscheidung 3)

---

## Ziel

Der Executor-Template bekommt OAuth2-Client-Credentials-Support. Token-Austausch, Caching und Refresh bei 401/Expiry laufen über einen neuen `IOAuthTokenCache` in Infrastructure. Authorization-Code/Implicit-Flow sind **explizit ausgeschlossen** (v1).

---

## Neue Dateien

### `Knotarium.Core/Contracts/OpenApi/IOAuthTokenCache.cs`

```csharp
public interface IOAuthTokenCache
{
    /// Returns a valid access token, fetching or refreshing as needed.
    Task<string> GetTokenAsync(
        string cacheKey,           // z.B. serverConfigId + credentialRef
        string tokenUrl,
        string clientId,
        string clientSecret,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default);

    /// Invalidates the cached token (call on 401).
    void Invalidate(string cacheKey);
}
```

### `Knotarium.Infrastructure/OpenApi/InMemoryOAuthTokenCache.cs`

Implementiert `IOAuthTokenCache`:
- Cache: `ConcurrentDictionary<string, CachedToken>` mit `(AccessToken, ExpiresAt)`.
- `GetTokenAsync`: Prüft Cache → noch gültig (mit 30s-Puffer) → return; sonst token endpoint POST → cache → return.
- `Invalidate`: Entfernt den Eintrag aus dem Cache.
- Token-Endpoint-Aufruf via `IHttpClientFactory` (kein `context.Http` — Infrastructure-Schicht).
- Response-Parsing: `access_token` + `expires_in` aus JSON.

### Executor-Template-Erweiterung (in `OpenApiNodeGenerator`)

Auth-Branch für `"oauth2"`:
```
1. Lade SecurityScheme → TokenUrl
2. Resolve credential → client_id:client_secret (Format: "clientId:clientSecret" in Credential Value)
3. IOAuthTokenCache.GetTokenAsync(cacheKey, tokenUrl, clientId, clientSecret, scopes)
4. Authorization: Bearer <token>
5. Bei HTTP 401: IOAuthTokenCache.Invalidate(cacheKey) → retry einmalig
```

---

## Tests

**Projekt:** `Knotarium.Tests`  
**Datei:** `Knotarium.Tests/OpenApi/OAuthTokenCacheTests.cs`  
Datei erweitern: `Knotarium.Tests/OpenApi/RestCallerExecutorTests.cs` (OAuth2-Szenarien)

### Token-Cache-Tests (`OAuthTokenCacheTests.cs`)

| Test | Szenario | Erwartung |
|---|---|---|
| `GetToken_FirstCall_FetchesFromEndpoint` | leerer Cache | HTTP-POST an TokenUrl |
| `GetToken_SecondCall_ReturnsCachedToken` | nach erstem Call | kein zweiter HTTP-POST |
| `GetToken_ExpiredToken_RefetchesFromEndpoint` | Token läuft ab (SimulateExpiry) | erneuter HTTP-POST |
| `Invalidate_ThenGet_FetchesFromEndpoint` | Invalidate + GetToken | erneuter HTTP-POST |
| `GetToken_EndpointReturnsError_ThrowsException` | HTTP 400 vom Token-Endpoint | Exception mit Meldung |

### Executor-OAuth2-Tests (Ergänzung in `RestCallerExecutorTests.cs`)

| Test | Szenario | Erwartung |
|---|---|---|
| `Execute_Auth_OAuth2_InjectsBearerToken` | SecuritySchemeType=oauth2 | `Authorization: Bearer <token>` |
| `Execute_Auth_OAuth2_On401_Retries` | Erster Call 401, Zweiter 200 | Retry, Token invalidiert, Erfolg |
| `Execute_Auth_OAuth2_On401_RetriesOnlyOnce` | Beide Calls 401 | Output "error", kein Loop |

---

## Definition of Done

- [ ] `dotnet build` (Solution) ohne Fehler
- [ ] Alle neuen Tests grün
- [ ] Authorization-Code/Implicit: explizit mit `NotSupportedException` oder klarem Fehlertext abgelehnt
- [ ] `IOAuthTokenCache` registriert als Singleton in DI
- [ ] Keine Regressions
