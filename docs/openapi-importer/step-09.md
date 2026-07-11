# Step 09 — Frontend: Importer + Operation/Schema-Browser

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 04](step-04.md) grün (API-Endpoints vorhanden)  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §8 (Frontend-Arbeit)

---

## Ziel

Neue React-Komponenten für Upload/Paste eines Specs und Anzeige der gruppierten Operationen und Schemas. Kein Drag-and-Drop in diesem Schritt (kommt Step 10).

---

## Neue Typen in `Frontend/src/types.ts`

```typescript
export interface ImportedSpec {
  id: string;
  title: string;
  apiVersion: string;
  latestVersionNumber: number;
  importedAtUtc: string;
}

export interface ApiParameter {
  name: string;
  in: 'path' | 'query' | 'header' | 'cookie';
  required: boolean;
  description?: string;
  schemaJson: string;
}

export interface ApiRequestBody {
  required: boolean;
  mediaTypes: string[];
  schemaJson: string;
}

export interface ApiOperation {
  operationId: string;
  method: string;
  pathTemplate: string;
  summary?: string;
  tags: string[];
  parameters: ApiParameter[];
  requestBody?: ApiRequestBody;
}

export interface OperationGroup {
  tag: string;
  operations: ApiOperation[];
}

export interface ApiSchema {
  name: string;
  description?: string;
  schemaJson: string;
}

export interface SpecDetail {
  id: string;
  title: string;
  groups: OperationGroup[];
  schemas: ApiSchema[];
}
```

---

## Neue API-Client-Helpers

**`Frontend/src/utils/openApiClient.ts`** (eng, keine Abstraktion-Overengineering):

```typescript
export async function importSpec(content: string | File): Promise<ImportedSpec>
export async function listSpecs(): Promise<ImportedSpec[]>
export async function getSpecDetail(id: string): Promise<SpecDetail>
export async function getOperation(specId: string, operationId: string): Promise<ApiOperation>
```

---

## Neue Komponenten

**`Frontend/src/components/OpenApiImporter.tsx`**
- Textarea für Paste + File-Upload-Button
- Submit → `importSpec()` → bei Erfolg: `onImported(spec)` Callback
- Fehlermeldung anzeigen (z.B. externe $ref abgelehnt)

**`Frontend/src/components/OperationBrowser.tsx`**
- Props: `specId: string`
- Lädt `SpecDetail` via `getSpecDetail()`
- Zeigt Operationen gruppiert nach Tag, zusammenklappbar
- Jede Operation: HTTP-Method-Badge (GET=grün, POST=blau, DELETE=rot etc.) + Pfad + Summary
- Noch kein Drag-Handle (kommt Step 10)

**`Frontend/src/components/SchemaList.tsx`**
- Props: `schemas: ApiSchema[]`
- Zeigt Namen und Description; Schema-JSON ausklappbar

---

## Tests

**Framework:** Vitest (analog bestehende Frontend-Tests, `Frontend/src/**/*.test.tsx`)

**Dateien:**
- `Frontend/src/components/OpenApiImporter.test.tsx`
- `Frontend/src/components/OperationBrowser.test.tsx`

### `OpenApiImporter`-Tests

| Test | Szenario | Erwartung |
|---|---|---|
| `renders_textarea_and_upload_button` | Mount | Textarea + Button vorhanden |
| `submit_with_empty_content_shows_error` | Submit ohne Inhalt | Fehlermeldung sichtbar |
| `submit_valid_yaml_calls_importSpec` | Mock `importSpec`, Submit | `importSpec` aufgerufen |
| `successful_import_calls_onImported` | Mock gibt Spec zurück | `onImported` aufgerufen mit Spec |
| `api_error_shows_error_message` | Mock wirft Fehler | Fehlermeldung angezeigt |

### `OperationBrowser`-Tests

| Test | Szenario | Erwartung |
|---|---|---|
| `renders_loading_state` | vor API-Response | Ladeindikator vorhanden |
| `renders_groups_from_api` | Mock gibt 2 Gruppen | 2 Tag-Gruppen angezeigt |
| `renders_operation_method_and_path` | Mock gibt GET /pets | "GET" + "/pets" angezeigt |
| `group_collapse_toggle_works` | Klick auf Tag-Header | Operationen ausgeblendet/eingeblendet |
| `api_error_shows_error_state` | Mock wirft Fehler | Fehlerzustand angezeigt |

---

## Definition of Done

- [ ] `npm run build` ohne Fehler
- [ ] Alle Vitest-Tests für Importer und OperationBrowser grün
- [ ] Keine Regressions (bestehende Frontend-Tests grün)
- [ ] Manuelle Verifikation: Spec importieren + Operationen anzeigen funktioniert im Browser
