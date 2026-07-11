# OpenAPI Importer — Implementierungsfortschritt

Jeder Schritt ist **erst abgeschlossen, wenn alle zugehörigen Tests grün sind**.  
Detaildokument: `docs/openapi-importer/step-XX.md`  
Plangrundlage: `architecture/OpenAPI_Importer_Plan.md`

---

## Schritte

- [x] **[Step 01](step-01.md) — Core Contracts & Normalized Model** ✅ 2026-06-07  
  `IOpenApiParser`, `IOpenApiSpecStore`, `IServerConfigStore` + alle Domain-Records in `KnotGarden.Core`.

- [x] **[Step 02](step-02.md) — Parser-Adapter (Infrastructure)** ✅ 2026-06-07  
  `Microsoft.OpenApi`-Adapter hinter `IOpenApiParser`: Swagger 2.0 → OpenAPI 3.x Up-Convert, externe `$ref`-Ablehnung, JSON + YAML. Unit-Tests mit Petstore-Fixtures (2.0 / 3.0 / 3.1).

- [x] **[Step 03](step-03.md) — Persistence (EF-Entities & Stores)** ✅ 2026-06-07  
  EF-Entities `OpenApiSpec`, `OpenApiSpecVersion`, `ServerConfig` in `AppDbContext`; Store-Implementierungen; Dev-DB neu erstellen.

- [x] **[Step 04](step-04.md) — Import- & List-API** ✅ 2026-06-07  
  `POST /api/openapi/specs`, `GET /api/openapi/specs`, `GET /api/openapi/specs/{id}`, Versions-Endpoints. `ImportOpenApiSpecHandler` in `Features`. API-Tests analog `WorkflowApiTests`. `Microsoft.AspNetCore.OpenApi` entfernt (v1/v2 Laufzeitkonflikt).

- [x] **[Step 05](step-05.md) — Server-Configurations API** ✅ 2026-06-07  
  Entity, Store, `GET/POST/PUT/DELETE /api/server-configs`, Credential-Ref-Reuse; API-Tests.

- [x] **[Step 06](step-06.md) — OpenApiNodeGenerator** ✅ 2026-06-07  
  Deterministisches Template-Emitting: `GeneratedPackage` (Manifest mit `operationId`-Dropdown + Executor-Source). Compile/Store/Registry-Anbindung. Generator-Unit-Tests.

- [x] **[Step 07](step-07.md) — Executor: Request-Build & Auth (API Key / Bearer / Basic)** ✅ 2026-06-07  
  Generierter `INodeExecutor`: URL-Aufbau, Path/Query/Header/Body-Platzierung, omit optional args, API-Key/Bearer/Basic-Auth. Unit-Tests mit Mock `IHttpClient` / `ICredentialAccessor`.

- [x] **[Step 08](step-08.md) — OAuth2 Client-Credentials + Token-Cache** ✅ 2026-06-07  
  Token-Endpoint-Exchange, `OAuthTokenCache` (Infrastructure), Refresh bei 401/Expiry. Unit-Tests.

- [x] **[Step 09](step-09.md) — Frontend: Importer + Operation/Schema-Browser** ✅ 2026-06-07  
  `OpenApiImporter` (Upload/Paste), `OperationBrowser` (grouped, collapsible), `SchemaList` (expandable JSON); `OpenApiView` als koordinierender View; neue Typen in `types.ts`; `openApiClient.ts` API-Helpers; „API Importer"-Tab in App.tsx. 10 Vitest-Tests grün, 58/58 total.

- [x] **[Step 10](step-10.md) — Frontend: Drag-and-Drop + Dynamisches Property-Form** ✅ 2026-06-07  
  Drag-Source → Canvas-Drop → `restCaller`-Node mit vorausgewählter `operationId`; dynamisches Argument-Form; `operationId`-Wechsel re-rendert Form. Vitest-Tests.

- [x] **[Step 11](step-11.md) — Frontend: Server-Config-UI** ✅ 2026-06-07  
  `ServerConfigManager`-Komponente; Inline „Create from spec server"; Dropdown in Property-Form. Vitest-Tests.

- [ ] **[Step 12](step-12.md) — End-to-End-Verifikation**  
  Petstore in allen drei Dialekten importieren, Node im Canvas erzeugen, gegen Mock-Server ausführen. Playwright-E2E analog `Frontend/e2e`.

---

## Definition of Done (pro Schritt)

1. Code kompiliert ohne Warnings (`dotnet build` / `npm run build`).
2. Alle Tests des Schritts sind grün (`dotnet test` / `npm test`).
3. Keine Regressions in bestehenden Tests.
4. Checkbox oben abhaken + Datum eintragen.
