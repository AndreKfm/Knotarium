# Step 07 — Executor: Request-Build & Auth (API Key / Bearer / Basic)

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 06](step-06.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §6 (Auth-Tabelle), §5 (Executor-Logik Feature 5)

---

## Ziel

Den vom Generator erzeugten Executor-Template-Code so implementieren, dass er zur Laufzeit:
1. `operationId` → Operation aus `IOpenApiSpecStore` lädt
2. `serverConfigId` → `ServerConfig` + `CredentialRef` auflöst
3. `arguments`-JSON → Path / Query / Header / Body platziert
4. Auth-Header für **API Key**, **Bearer** und **Basic** hinzufügt
5. Request via `context.Http` sendet → `success` oder `error` Output

Der Executor-Code lebt als Template-String in `OpenApiNodeGenerator` und wird von Roslyn kompiliert. Die Tests testen den **kompilierten und ausgeführten** Executor (nicht nur den String).

### Refinement F: DI-Auflösung im kompilierten Executor

`DynamicCustomNodeTask` instanziiert Executors heute mit `Activator.CreateInstance(type)` (parameterlos). Der generierte Executor braucht aber `IOpenApiSpecStore`, `IServerConfigStore` und `IOAuthTokenCache`.

**Lösung — Constructor-Matching in `DynamicCustomNodeTask`** (kein `IServiceProvider` auf `INodeContext`):

1. `DynamicCustomNodeTask` bekommt die drei Services per Konstruktor-Injection.
2. Zeile 146 (`Activator.CreateInstance`) wird ersetzt durch Reflection-basiertes Constructor-Matching:
   - Gibt es einen Konstruktor dessen Parameter alle aus den bekannten Services auflösbar sind → diesen verwenden.
   - Sonst → parameterloser Fallback (alle bestehenden Executors unverändert).
3. Der generierte Executor-Template deklariert einen Konstruktor mit genau diesen drei Typen.
4. `BuildReferences()` muss die Assemblies der drei Service-Interfaces einschließen.

```csharp
// In DynamicCustomNodeTask.ExecuteAsync, ersetzt Zeile 146:
var knownServices = new Dictionary<Type, object?>
{
    [typeof(IOpenApiSpecStore)]  = _openApiSpecStore,
    [typeof(IServerConfigStore)] = _serverConfigStore,
    [typeof(IOAuthTokenCache)]   = _oAuthTokenCache,
};
var ctor = cacheEntry.Type.GetConstructors()
    .FirstOrDefault(c => c.GetParameters().All(p => knownServices.ContainsKey(p.ParameterType)));
executor = ctor != null
    ? (INodeExecutor)ctor.Invoke(ctor.GetParameters().Select(p => knownServices[p.ParameterType]).ToArray())
    : (INodeExecutor)Activator.CreateInstance(cacheEntry.Type)!;
```

`INodeContext` bleibt unverändert — kein Service-Locator, keine Core-Contract-Änderung.

---

## Executor-Laufzeitlogik (implementiert im Template)

```
1. Parse arguments JSON → { path: {}, query: {}, header: {}, body: {} }

2. Load ParsedSpec (Refinement B + D):
   a. If specVersion is set and parseable as int → IOpenApiSpecStore.GetVersionAsync(specId, version)
   b. Otherwise → IOpenApiSpecStore.GetLatestAsync(specId)
   c. Find ApiOperation by operationId within loaded ParsedSpec.Operations
   d. Lookup SecurityScheme details from ParsedSpec.SecuritySchemes by name
      (needed for apiKey In/ParamName — Refinement D)

3. Build URL:
   a. Load ServerConfig by serverConfigId via IServerConfigStore
   b. Substitute ServerVariables into BaseUrl template (Refinement C):
      foreach (var kv in config.ServerVariables) baseUrl = baseUrl.Replace("{" + kv.Key + "}", kv.Value);
   c. Substitute path args into PathTemplate → replace {param} from path-args
   d. Append non-empty query args as ?key=value

4. Build HttpRequestMessage:
   a. Method from operation.Method
   b. Header args → request.Headers
   c. Body (if present) → StringContent with content-type from operation.RequestBody.MediaTypes[0]

5. Auth (scheme details from ParsedSpec.SecuritySchemes — Refinement D):
   a. SecuritySchemeType == "apiKey":
      - Lookup SecurityScheme → In + ParamName
      - In == "header" → request.Headers.Add(paramName, secret)
      - In == "query"  → append &paramName=secret to URL
   b. SecuritySchemeType == "http_bearer" → Authorization: Bearer <secret>
   c. SecuritySchemeType == "http_basic" (Refinement A):
      - Retrieve decrypted credential value (format: "username:password")
      - Split on first ':' → username, password
      - Authorization: Basic base64(username:password)

6. context.Http.SendAsync(request)
7. Success (2xx) → output "success" = { statusCode, headers, body }
   Error → output "error" = { statusCode, body }
```

---

## Tests

**Projekt:** `Knotarium.Tests`  
**Datei:** `Knotarium.Tests/OpenApi/RestCallerExecutorTests.cs`

Strategie: `OpenApiNodeGenerator.Generate()` aufrufen → den `ExecutorCode` mit Roslyn kompilieren → `INodeExecutor`-Instanz erzeugen → `ExecuteAsync` aufrufen mit Mock-`INodeContext`. Analog zu bestehenden `HttpRequestNodeTaskTests`.

### Mocks benötigt

- `IHttpClient` (Fake analog `FakeHttpMessageHandler` in `HttpRequestNodeTaskTests`)
- `ICredentialAccessor` (Fake Dictionary-Lookup)
- `IOpenApiSpecStore` (Fake, gibt vollständige `ParsedSpec` zurück — inkl. SecuritySchemes; Refinement D)
- `IServerConfigStore` (Fake, gibt bekannte ServerConfig inkl. ServerVariables zurück)
- `INodeContext` (Fake mit diesen Services)

### Testmatrix

| Test | Szenario | Erwartung |
|---|---|---|
| `Execute_GetOperation_BuildsCorrectUrl` | GET /pets/{id}, path arg = "42" | Request-URL endet mit `/pets/42` |
| `Execute_QueryArgs_AppendedToUrl` | GET /pets, query arg status="available" | URL enthält `?status=available` |
| `Execute_HeaderArgs_AddedToRequest` | Header arg X-Custom="val" | Request enthält Header `X-Custom: val` |
| `Execute_PostWithBody_SetsContentType` | POST /pets, body JSON | Content-Type = `application/json` |
| `Execute_OmittedOptionalArg_NotSentAsQuery` | Optionales Query-Arg nicht in arguments | URL enthält param nicht |
| `Execute_Auth_ApiKeyHeader_InjectsHeader` | SecuritySchemeType=apiKey, In=header | Header enthält Key |
| `Execute_Auth_ApiKeyQuery_InjectsQueryParam` | SecuritySchemeType=apiKey, In=query | URL enthält Key als Query-Param |
| `Execute_Auth_Bearer_InjectsAuthHeader` | SecuritySchemeType=http_bearer | `Authorization: Bearer <secret>` |
| `Execute_Auth_Basic_InjectsAuthHeader` | Credential="alice:s3cr3t" | `Authorization: Basic YWxpY2U6czNjcjN0` |
| `Execute_Auth_Basic_PasswordWithColon_SplitsOnFirstColon` | Credential="user:p:ass" | username="user", password="p:ass" |
| `Execute_ServerVariables_SubstitutedInBaseUrl` | BaseUrl="https://{env}.api.com", vars={env:prod} | URL beginnt mit `https://prod.api.com` |
| `Execute_SpecVersion_Pinned_LoadsCorrectVersion` | specVersion="2" | `GetVersionAsync(..., 2)` aufgerufen |
| `Execute_SpecVersion_NotSet_LoadsLatest` | specVersion leer | `GetLatestAsync` aufgerufen |
| `Execute_SuccessResponse_ReturnsSuccessOutput` | HTTP 200 | Output "success" gesetzt |
| `Execute_ErrorResponse_ReturnsErrorOutput` | HTTP 400 | Output "error" gesetzt |
| `Execute_MissingOperationId_ReturnsError` | operationId nicht in Store | Output "error" mit Meldung |
| `Execute_MissingServerConfig_ReturnsError` | serverConfigId nicht in Store | Output "error" mit Meldung |
| `DynamicCustomNodeTask_ExistingExecutors_StillUseParameterlessCtor` | Executor ohne passenden Ctor | Fallback auf `Activator.CreateInstance`, kein Fehler |

---

## Definition of Done

- [ ] `dotnet build` (Solution) ohne Fehler
- [ ] Alle `RestCallerExecutorTests`-Tests grün
- [ ] OAuth2 absichtlich ausgelassen (kommt Step 08)
- [ ] Keine Regressions
