# Step 12 — End-to-End-Verifikation

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 11](step-11.md) grün (alle vorangegangenen Schritte grün)  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §9 (Schritt 12), §10 (Testing & Verification)

---

## Ziel

Ein vollständiger E2E-Test-Durchlauf mit Playwright, der den gesamten Feature-Pfad abdeckt: Import → Node in Palette → Drag auf Canvas → Server-Config → Ausführung gegen Mock-Server. Analog bestehende Tests unter `Frontend/e2e/`.

---

## Voraussetzungen

- Mock-HTTP-Server für die Petstore-API (z.B. mit `msw` oder einem simplen Express-Script im Test-Setup).
- Alle drei Petstore-Fixtures (2.0, 3.0, 3.1) als Testdaten.

---

## E2E-Szenarien

**Datei:** `Frontend/e2e/openApiImporter.spec.ts`

### Szenario 1: Import Swagger 2.0 JSON

```
1. Navigiere zu Import-Screen
2. Paste petstore-swagger20.json Inhalt
3. Klicke "Import"
4. Erwarte: Erfolgsmeldung, Spec-Liste zeigt "Petstore" mit Version 1
5. Erwarte: In Palette erscheint Node "Petstore API"
```

### Szenario 2: Import OpenAPI 3.0 YAML

```
1. Upload petstore-openapi30.yaml via File-Upload
2. Klicke "Import"
3. Erwarte: Erfolgsmeldung
4. Erwarte: Spec bereits vorhanden → Version 2 (Re-Import)
```

### Szenario 3: Import OpenAPI 3.1 JSON

```
1. Paste petstore-openapi31.json Inhalt
2. Klicke "Import"
3. Erwarte: Erfolgsmeldung, Version 3
```

### Szenario 4: External-$ref abgelehnt

```
1. Paste external-ref.yaml Inhalt
2. Klicke "Import"
3. Erwarte: Fehlermeldung mit "External $ref"
4. Erwarte: kein neuer Eintrag in Spec-Liste
```

### Szenario 5: Drag-and-Drop + Konfiguration

```
1. Spec vorhanden (aus Szenario 1)
2. Öffne OperationBrowser für Petstore
3. Ziehe "GET /pet/{petId}" auf Canvas
4. Erwarte: Node "Petstore API" auf Canvas, operationId = "getPetById"
5. Property-Form zeigt: Path-Param "petId" (required)
6. Erstelle Server-Config mit BaseUrl http://localhost:3456, SecuritySchemeType=none
7. Wähle Server-Config im Property-Form aus
8. Setze petId = "42"
9. Klicke "Run" (oder führe Workflow aus)
10. Erwarte: Mock-Server erhält GET /pet/42
11. Erwarte: Node-Output "success" mit statusCode 200
```

### Szenario 6: operationId-Wechsel im Property-Form

```
1. Node "Petstore API" auf Canvas (operationId = "getPetById")
2. Ändere operationId auf "addPet" im Dropdown
3. Erwarte: Formular zeigt nun Body-Felder (kein petId mehr)
```

---

## Mock-Server Setup

In `Frontend/e2e/` ein `mockPetstore.ts` anlegen:
```typescript
// Startet einen lokalen HTTP-Server auf Port 3456 der:
// GET /pet/:id → 200 { id, name: "Buddy" }
// POST /pet    → 200 { id: 123, name: "New Pet" }
// GET /pet/findByStatus → 200 [{ id: 1, name: "Buddy" }]
```

Setup/Teardown in `playwright.config.ts` oder `globalSetup.ts`.

---

## Definition of Done

- [ ] `npx playwright test openApiImporter` — alle Szenarien grün
- [ ] Alle drei Petstore-Dialekte durchlaufen erfolgreich
- [ ] Mock-Server-Fixture committet und dokumentiert
- [ ] Keine Regressions (bestehende E2E-Tests grün)
- [ ] **Gesamtes Feature freigegeben:** alle Steps 01–12 abgehakt in [PROGRESS.md](PROGRESS.md)
