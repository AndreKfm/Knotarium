# Step 11 — Frontend: Server-Config-UI

**Status:** ✅ Abgeschlossen (2026-06-07)  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 05](step-05.md) und [Step 10](step-10.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §6 (ServerConfig-UI), §8 (Frontend)

---

## Ziel

Eine `ServerConfigManager`-Komponente (CRUD-Screen analog Credentials-UI) und eine Inline-„Create from spec server"-Affordanz im Importer-Flow.

---

## Neue Typen in `Frontend/src/types.ts`

```typescript
export interface ServerConfigInfo {
  id: string;
  name: string;
  baseUrl: string;
  securitySchemeType: 'none' | 'apiKey' | 'http_bearer' | 'http_basic' | 'oauth2';
  credentialRef?: string;
  createdAt: string;
  updatedAt: string;
}

export interface CreateServerConfigRequest {
  name: string;
  baseUrl: string;
  securitySchemeType: string;
  credentialRef?: string;
}
```

---

## Neue API-Client-Helper

```typescript
// Frontend/src/utils/serverConfigClient.ts
export async function listServerConfigs(): Promise<ServerConfigInfo[]>
export async function createServerConfig(req: CreateServerConfigRequest): Promise<ServerConfigInfo>
export async function updateServerConfig(id: string, req: CreateServerConfigRequest): Promise<ServerConfigInfo>
export async function deleteServerConfig(id: string): Promise<void>
```

---

## Neue Komponenten

**`Frontend/src/components/ServerConfigManager.tsx`**
- Liste aller Server-Configs (Name, BaseUrl, SecuritySchemeType)
- „New"-Button → Inline-Formular oder Modal
- Edit-Button → Formular vorausgefüllt
- Delete-Button → Bestätigungsdialog → DELETE
- Credential-Dropdown: zeigt existierende Credentials (bestehenden API-Client verwenden)

**Erweiterung `OperationBrowser.tsx`**  
Nach erfolgreichem Import: „Use this spec's server" → öffnet `ServerConfigManager` im Create-Modus, Base-URL vorausgefüllt aus `spec.DefaultServers[0]`.

---

## Tests

**Datei:** `Frontend/src/components/ServerConfigManager.test.tsx`

| Test | Szenario | Erwartung |
|---|---|---|
| `renders_empty_list` | keine Configs | leere Liste + "New"-Button |
| `renders_existing_configs` | Mock gibt 2 Configs | 2 Einträge mit Name + BaseUrl |
| `create_config_calls_api` | Formular ausfüllen + Submit | `createServerConfig` aufgerufen |
| `create_success_shows_new_entry` | Mock gibt neue Config | neue Config in Liste |
| `create_validation_error_empty_name` | Submit ohne Name | Fehlermeldung |
| `create_validation_error_empty_baseUrl` | Submit ohne BaseUrl | Fehlermeldung |
| `delete_calls_api_after_confirm` | Delete-Button + Bestätigen | `deleteServerConfig` aufgerufen |
| `delete_cancel_does_not_call_api` | Delete-Button + Abbrechen | kein API-Call |
| `edit_prefills_form` | Edit-Button klicken | Formular mit bestehenden Werten |
| `edit_submit_calls_updateServerConfig` | Edit + Submit | `updateServerConfig` aufgerufen |

---

## Definition of Done

- [x] `npm run build` ohne Fehler
- [x] Alle `ServerConfigManager`-Tests grün
- [x] Manuelle Verifikation: Server-Config anlegen, in Property-Form auswählen
- [x] Inline-„Create from spec server" funktioniert mit vorausgefüllter URL
- [x] Keine Regressions
