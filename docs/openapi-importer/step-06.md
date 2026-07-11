# Step 06 — OpenApiNodeGenerator

**Status:** ☐ Offen  
**Übersicht:** [PROGRESS.md](PROGRESS.md)  
**Voraussetzung:** [Step 04](step-04.md) grün  
**Planreferenz:** `architecture/OpenAPI_Importer_Plan.md` §5 (Node-Modell), §9 Build-Order Schritt 6

---

## Ziel

`OpenApiNodeGenerator` (in `Knotarium.Features/OpenApi/`) erzeugt deterministisch aus einer `ParsedSpec` ein `GeneratedPackage` (bestehender Record aus `INodePackageGenerator`). Dieses Package wird direkt durch den **bestehenden** Roslyn-Compile-Pfad in `DynamicCustomNodeTask` kompiliert und via `DbNodePackageManifestProvider` im Palette sichtbar — keine neue Registry-Infrastruktur.

Der Generator wird am Ende von `ImportOpenApiSpecHandler.HandleAsync` aufgerufen und das Ergebnis über den bestehenden Node-Package-Speicherpfad persistiert.

---

## Neue Dateien

### `Knotarium.Features/OpenApi/OpenApiNodeGenerator.cs`

```csharp
public sealed class OpenApiNodeGenerator
{
    /// <summary>
    /// Creates a GeneratedPackage from a parsed spec.
    /// The PackageId is derived deterministically from the spec Id:
    ///   "openapi." + spec.Id.Value.ToLowerInvariant()
    /// </summary>
    public GeneratedPackage Generate(ParsedSpec spec);
}
```

**Manifest-Template** (YAML-String, per Spec individualisiert):

```yaml
id: openapi.{specId}
displayName: {spec.Title}
category: Integrations
tier: Compiled
sideEffectKind: NonIdempotentSideEffect
recoveryMode: RetryAutomatically
capabilities: [http, credentials]
parameters:
  - name: operationId
    type: string
    required: true
    expression: false
    values: [{komma-separierte OperationIds}]
  - name: serverConfigId
    type: string
    required: true
    expression: false
  - name: specVersion
    type: string
    required: false
    expression: false
  - name: arguments
    type: string
    required: false
    expression: true
outputs:
  - name: success
  - name: error
```

**Executor-Template** (C#-String mit Platzhaltern):

Der generierte Executor-Code ist ein vollständiges `INodeExecutor`-Klassengerüst (kein `INodeTask` — `DynamicCustomNodeTask` erwartet `INodeExecutor`). Baked-in ist die `SpecId`; alle anderen Daten werden zur Laufzeit aus DI geladen.

Wichtige Platzhalter:
- `{{SPEC_ID}}` — unveränderliche Id des Specs
- Kein operationsbezogener Code direkt im Template — der Executor liest die Operation zur Laufzeit via `IOpenApiSpecStore`

Der Template-String liegt als `const string` oder embedded Ressource in `OpenApiNodeGenerator`. Keine LLM-Beteiligung.

### Wiring in `ImportOpenApiSpecHandler`

Nach `store.SaveAsync(parsed, ct)`:
1. `generator.Generate(parsed)` → `GeneratedPackage`
2. Bestehenden `INodePackageCompiler`-/Compile-Pfad aufrufen (analog wie es bei Custom Node Packages läuft — `DynamicCustomNodeTask` oder direkter Aufruf des Package-Store-Pfads aus `Program.cs` Node-Package-Install-Endpoint)
3. Bei Re-Import: neue `NodePackageVersion` — analog bestehender Versionierung.

> **Frage an Impl.-Agent:** Schau dir `Program.cs` Node-Package-Install-Endpoint (ca. Zeile 1331) an und prüfe, welcher Service den `GeneratedPackage`-Record entgegennimmt und in `NodePackages`/`NodePackageVersions` speichert. Nutze genau denselben Pfad.

---

## Tests

**Projekt:** `Knotarium.Tests`  
**Datei:** `Knotarium.Tests/OpenApi/NodeGeneratorTests.cs`

Der Generator ist **rein deterministisch** → keine Mocks nötig, nur Input/Output-Prüfung.

### Testmatrix

| Test | Szenario | Erwartung |
|---|---|---|
| `Generate_PackageId_IsPrefixedWithOpenApi` | beliebige Spec | `PackageId` beginnt mit `"openapi."` |
| `Generate_ManifestYaml_ContainsAllOperationIds` | Spec mit 3 Operationen | alle 3 OperationIds in `values:` |
| `Generate_ManifestYaml_DisplayNameMatchesSpecTitle` | `Title = "Petstore"` | `displayName: Petstore` |
| `Generate_ManifestYaml_CategoryIsIntegrations` | beliebige Spec | `category: Integrations` |
| `Generate_ManifestYaml_TierIsCompiled` | beliebige Spec | `tier: Compiled` |
| `Generate_ManifestYaml_HasServerConfigIdParam` | beliebige Spec | Parameter `serverConfigId` vorhanden |
| `Generate_ManifestYaml_HasArgumentsParam_WithExpression` | beliebige Spec | `arguments`-Parameter mit `expression: true` |
| `Generate_ExecutorCode_ContainsSpecId` | `SpecId = "petstore"` | `ExecutorCode` enthält `"petstore"` als Literal |
| `Generate_ExecutorCode_IsValidCSharpSyntax` | beliebige Spec | Roslyn-SyntaxTree ohne Parse-Fehler |
| `Generate_TwoCallsSameSpec_ProduceSameOutput` | gleiche Spec zweimal | Byte-identische Ausgabe |
| `Generate_SpecWithNoOperations_ManifestHasEmptyValues` | `Operations = []` | `values: []` in Manifest |
| `Generate_SpecId_IsNormalized` | `Title = "My API!!"` | PackageId enthält keine Sonderzeichen |

### Beispiel-Testcode

```csharp
public class NodeGeneratorTests
{
    private readonly OpenApiNodeGenerator _generator = new();

    private static ParsedSpec BuildSpec(string id, string title, params string[] operationIds)
    {
        var ops = operationIds
            .Select(oid => new ApiOperation(oid, "GET", "/x", null, [], [], null, []))
            .ToList();
        return new ParsedSpec(
            new ImportedSpec(new OpenApiSpecId(id), title, "1.0", "openapi3.0",
                [], [], DateTimeOffset.UtcNow, 1),
            ops, [], []);
    }

    [Fact]
    public void Generate_PackageId_IsPrefixedWithOpenApi()
    {
        var pkg = _generator.Generate(BuildSpec("petstore", "Petstore"));
        Assert.StartsWith("openapi.", pkg.PackageId);
    }

    [Fact]
    public void Generate_ManifestYaml_ContainsAllOperationIds()
    {
        var pkg = _generator.Generate(BuildSpec("api", "My API", "getUser", "listUsers", "deleteUser"));
        Assert.Contains("getUser", pkg.ManifestYaml);
        Assert.Contains("listUsers", pkg.ManifestYaml);
        Assert.Contains("deleteUser", pkg.ManifestYaml);
    }

    [Fact]
    public void Generate_ExecutorCode_IsValidCSharpSyntax()
    {
        var pkg = _generator.Generate(BuildSpec("api", "My API", "listItems"));
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(pkg.ExecutorCode);
        Assert.Empty(tree.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void Generate_TwoCallsSameSpec_ProduceSameOutput()
    {
        var spec = BuildSpec("api", "My API", "op1");
        var a = _generator.Generate(spec);
        var b = _generator.Generate(spec);
        Assert.Equal(a.ManifestYaml, b.ManifestYaml);
        Assert.Equal(a.ExecutorCode, b.ExecutorCode);
    }
}
```

---

## Definition of Done

- [ ] `dotnet build Knotarium.Features` ohne Fehler
- [ ] Alle `NodeGeneratorTests`-Tests grün
- [ ] Nach Import eines Petstore-Specs erscheint ein Node in der Palette (manuell verifizieren)
- [ ] Keine Regressions
