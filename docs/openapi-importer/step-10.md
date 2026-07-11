# Step 10 — Frontend: Drag-and-Drop + Dynamisches Property-Form

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 09](step-09.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §5 (Drag-and-Drop UX), §8 (node-editor)

---

## Ziel

Operationen aus dem `OperationBrowser` können auf den Canvas gezogen werden. Das erzeugt einen REST-Caller-Node (das kompilierte API-Node-Package aus Step 06) mit vorausgewählter `operationId`. Das Property-Panel rendert ein dynamisches Formular für Path/Query/Header/Body-Argumente. Wechsel der `operationId` aktualisiert das Formular.

---

## Drag-and-Drop

### Drag-Source in `OperationBrowser.tsx`

Jede Operation bekommt ein `draggable`-Attribut. `onDragStart` serialisiert:
```json
{ "type": "openapi-operation", "specId": "...", "packageId": "openapi.petstore", "operationId": "getPetById" }
```
via `dataTransfer.setData("application/json", ...)`.

### Drop-Handler im Canvas

In `Frontend/src/node-editor/` (bestehende Drop-Handler suchen — analog wie andere Node-Typen aus der Palette gedroppt werden):

1. `dataTransfer.getData("application/json")` → parse → `type == "openapi-operation"`
2. Neuen Node anlegen mit:
   - `nodeType = packageId` (z.B. `"openapi.petstore"`)
   - `properties.operationId = operationId`
   - `properties.arguments = {}` (initial leer, wird durch Formular befüllt)
3. Canvas-Position aus Drop-Event.

---

## PropertiesPanel-Integration (Refinement E)

In `Frontend/src/node-editor/PropertiesPanel.tsx` (oder äquivalente Datei — vor Implementierung prüfen):

```typescript
// Bestehende Logik (vereinfacht):
// if (selectedNode) render <ManifestForm ... />

// Neu: openapi.* Nodes bekommen RestCallerPropertyForm
if (selectedNode?.type?.startsWith('openapi.')) {
  const specId = selectedNode.type.replace(/^openapi\./, '');
  return <RestCallerPropertyForm specId={specId} ... />;
}
// Sonst: bisheriges ManifestForm
```

Ohne diese Änderung würde der `arguments`-Parameter (Typ `string` im Manifest) als rohes Textfeld gerendert, statt als strukturiertes Path/Query/Header/Body-Formular.

---

## Dynamisches Property-Form

**`Frontend/src/components/RestCallerPropertyForm.tsx`**

Props:
```typescript
interface RestCallerPropertyFormProps {
  specId: string;
  operationId: string;
  arguments: Record<string, unknown>;
  onArgumentsChange: (args: Record<string, unknown>) => void;
  onOperationIdChange: (operationId: string) => void;
  serverConfigId?: string;
  onServerConfigIdChange: (id: string) => void;
}
```

Verhalten:
- Lädt Operation-Detail via `getOperation(specId, operationId)` bei Mount und bei `operationId`-Änderung.
- Rendert Felder gruppiert: **Path** / **Query** / **Header** / **Body**.
- Pflichtfelder markiert (`*`).
- Optionale Felder: leer = nicht gesendet.
- Jedes Feld ist expression-enabled (analog bestehender Expression-Input-Control — bestehende Komponente wiederverwenden).
- `operationId`-Dropdown: alle Operations aus Manifest-`values` (aus Node-Definition laden, oder aus gespeicherter Spec).
- `serverConfigId`-Dropdown: lädt `listServerConfigs()`.

---

## Neue API-Client-Helper

```typescript
// Frontend/src/utils/openApiClient.ts (ergänzen)
export async function listServerConfigs(): Promise<ServerConfigInfo[]>
```

---

## Tests

**Dateien:**
- `Frontend/src/components/RestCallerPropertyForm.test.tsx`
- `Frontend/src/node-editor/openApiDrop.test.ts` (Drop-Handler-Unit-Test)

### `RestCallerPropertyForm`-Tests

| Test | Szenario | Erwartung |
|---|---|---|
| `renders_path_params_for_operation` | GET /pets/{id} mit path-param "id" | Input für "id" unter "Path" |
| `renders_query_params_for_operation` | GET /pets mit query-param "status" | Input für "status" unter "Query" |
| `required_param_is_marked` | required=true | `*`-Markierung vorhanden |
| `optional_param_not_marked` | required=false | keine `*`-Markierung |
| `changing_operationId_reloads_form` | OperationId-Dropdown wechseln | `getOperation` erneut aufgerufen |
| `onArgumentsChange_called_on_input` | Eingabe in Feld | Callback mit aktuellem args-Objekt |
| `serverConfig_dropdown_shows_options` | Mock gibt 2 Configs | 2 Optionen im Dropdown |

### PropertiesPanel-Tests

| Test | Szenario | Erwartung |
|---|---|---|
| `renders_RestCallerPropertyForm_for_openapi_node` | selectedNode.type = "openapi.petstore" | `RestCallerPropertyForm` gerendert |
| `renders_ManifestForm_for_non_openapi_node` | selectedNode.type = "HttpRequest" | Standard `ManifestForm` gerendert |

### Drop-Handler-Tests

| Test | Szenario | Erwartung |
|---|---|---|
| `drop_openapi_operation_creates_node` | DragEvent mit korrekten Daten | Node mit korrektem nodeType + operationId |
| `drop_unknown_type_ignored` | DragEvent mit anderem type | kein neuer Node |

---

## Definition of Done

- [ ] `npm run build` ohne Fehler
- [ ] Alle Vitest-Tests grün
- [ ] Manuelle Verifikation: Operation draggen → Node auf Canvas → Property-Form zeigt korrekte Felder
- [ ] operationId-Wechsel im Panel aktualisiert das Formular ohne Seiteneffekte
- [ ] Keine Regressions
