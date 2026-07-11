# Polling Trigger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `pollingTrigger` node that periodically polls an external source (HTTP or OpenAPI operation), detects new/changed data via a per-trigger cursor, and starts a workflow run — passing the fetched payload in — only when something changed.

**Architecture:** Mirror the proven scheduler spine. A `PollingTrigger` table (analogous to `Schedule`) carries interval + cursor; a `WorkflowPollingTriggerSynchronizer` reconciles rows on workflow save; a `PollingWorker` background service (gated by `RuntimeArmingState` + per-workflow `IsEnabled`) drives `PollEvaluationService`, which polls via a pluggable `IPollSource` (HTTP first, OpenAPI second) and enqueues an `ExecutionInstance` only when change-detection reports new data. The executor surfaces the payload on the trigger node's `result` port.

**Tech Stack:** .NET / C#, EF Core (SQLite + Postgres), xUnit, React + TypeScript frontend. Spec: `docs/superpowers/specs/2026-06-14-polling-trigger-design.md`.

---

## File Structure

**Backend — new files**
- `Backend/Knotarium.Core/Domain/PollingTrigger.cs` — persisted trigger row (interval, next-poll, cursor, config, diagnostics).
- `Backend/Knotarium.Core/Contracts/IPollSource.cs` — source seam + `PollContext` / `PollResult` records + `PollChangeDetection` enum.
- `Backend/Knotarium.Core/Contracts/IPollEvaluationService.cs` — evaluation entry point.
- `Backend/Knotarium.Features/Polling/BodyChangeDetector.cs` — body-level change detection (hash / json-cursor / always).
- `Backend/Knotarium.Features/Polling/HttpPollSource.cs` — HTTP source impl (Phase 1).
- `Backend/Knotarium.Features/Polling/OpenApiPollSource.cs` — OpenAPI source impl (Phase 2).
- `Backend/Knotarium.Features/Polling/PollSourceRegistry.cs` — resolves `IPollSource` by `Kind`.
- `Backend/Knotarium.Features/Polling/PollEvaluationService.cs` — due selection + poll + conditional enqueue.
- `Backend/Knotarium.Api/Services/WorkflowPollingTriggerIdFactory.cs` — deterministic trigger id.
- `Backend/Knotarium.Api/Services/WorkflowPollingTriggerSynchronizer.cs` — reconcile rows on save.
- `Backend/Knotarium.Api/Services/PollingWorker.cs` — background loop.
- `nodes/PollingTrigger/manifest.yaml` — node manifest.

**Backend — modified files**
- `Backend/Knotarium.Infrastructure/Persistence/AppDbContext.cs` — add `DbSet<PollingTrigger>` + entity config.
- `Backend/Knotarium.Features/Execution/WorkflowExecutor.cs` — `CreateTriggerOutputs` + `IsTriggerCompatibleWithOrigin`.
- `Backend/Knotarium.Api/Program.cs` — DI registrations + wire synchronizer into save endpoints.

**Frontend — new/modified files**
- `Frontend/src/components/PollingTriggerPropertyForm.tsx` — config UI.
- `Frontend/src/components/PropertiesPanel.tsx` — route `pollingTrigger` to the new form.

**Test files**
- `Backend/Knotarium.Tests/Polling/PollingTriggerManifestTests.cs`
- `Backend/Knotarium.Tests/Polling/BodyChangeDetectorTests.cs`
- `Backend/Knotarium.Tests/Polling/HttpPollSourceTests.cs`
- `Backend/Knotarium.Tests/Polling/OpenApiPollSourceTests.cs`
- `Backend/Knotarium.Tests/Polling/WorkflowPollingTriggerSynchronizerTests.cs`
- `Backend/Knotarium.Tests/Polling/PollEvaluationServiceTests.cs`
- `Backend/Knotarium.Tests/Polling/PollingTriggerExecutorTests.cs`
- `Backend/Knotarium.Tests/Polling/PollingTestSupport.cs` — shared SQLite context + `FixedTimeProvider` helpers.

**Test command (all):** `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj`
**Test command (filtered):** `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`

---

## Task 1: Node manifest `pollingTrigger`

**Files:**
- Create: `nodes/PollingTrigger/manifest.yaml`
- Test: `Backend/Knotarium.Tests/Polling/PollingTriggerManifestTests.cs`

The manifest declares the node as trigger-only with its config parameters and a single `result` output port. The compiler's `IsBuiltInTriggerNodeType` already treats any node whose manifest has `triggerOnly: true` as a trigger; this test pins that the manifest loads and is flagged correctly.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using System.Threading;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Xunit;

namespace Knotarium.Tests.Polling;

public class PollingTriggerManifestTests
{
    [Fact]
    public async System.Threading.Tasks.Task PollingTrigger_ManifestIsTriggerOnly_WithResultOutput()
    {
        var provider = new InMemoryNodePackageManifestProvider();
        var manifest = await provider.GetManifestAsync(new NodePackageId("pollingTrigger"), CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.True(manifest!.TriggerOnly);
        Assert.Contains(manifest.Outputs, o => o.Name == "result");
        Assert.Contains(manifest.Parameters, p => p.Name == "intervalSeconds");
        Assert.Contains(manifest.Parameters, p => p.Name == "sourceKind");
        Assert.Contains(manifest.Parameters, p => p.Name == "changeDetection");
    }
}
```

> NOTE: `InMemoryNodePackageManifestProvider` is the same helper used by `SetVariableManifestTests`. Confirm it loads from the `nodes/` directory; if it only loads a fixed built-in set, add `pollingTrigger` to that set the same way `scheduler` is registered (search the provider for `"scheduler"`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollingTriggerManifestTests"`
Expected: FAIL (manifest null — file does not exist yet).

- [ ] **Step 3: Create the manifest**

`nodes/PollingTrigger/manifest.yaml`:

```yaml
id: pollingTrigger
displayName: Polling Trigger
version: 1.0.0
category: Trigger
triggerOnly: true
description: Periodically polls an external source and starts the workflow when new data is detected.
parameters:
  - name: intervalSeconds
    type: Number
    required: true
    expression: false
  - name: sourceKind
    type: String
    required: true
    expression: false
    enum: ["http", "openapi"]
  - name: changeDetection
    type: String
    required: true
    expression: false
    enum: ["etag", "last-modified", "hash", "json-cursor", "always"]
  - name: jsonCursorPath
    type: String
    required: false
    expression: false
  # --- http source fields ---
  - name: url
    type: String
    required: false
    expression: true
  - name: method
    type: String
    required: false
    expression: false
  - name: headersJson
    type: String
    required: false
    expression: false
  - name: apiKeySecretRef
    type: CredentialRef
    required: false
    expression: false
  # --- openapi source fields ---
  - name: serverConfigId
    type: String
    required: false
    expression: false
  - name: operationId
    type: String
    required: false
    expression: false
  - name: specVersion
    type: String
    required: false
    expression: false
outputs:
  - name: result
```

> NOTE: Match the exact `ParameterDefinition` field names/casing used by `nodes/HttpRequest/manifest.yaml` and `nodes/scheduler/manifest.yaml`. If the enum field is named differently (e.g. `values:` rather than `enum:`), use that. Read one existing manifest with an enum parameter first and copy its shape.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollingTriggerManifestTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add nodes/PollingTrigger/manifest.yaml Backend/Knotarium.Tests/Polling/PollingTriggerManifestTests.cs
git commit -m "feat(polling): add pollingTrigger node manifest"
```

---

## Task 2: `PollingTrigger` domain entity + persistence

**Files:**
- Create: `Backend/Knotarium.Core/Domain/PollingTrigger.cs`
- Create: `Backend/Knotarium.Tests/Polling/PollingTestSupport.cs`
- Modify: `Backend/Knotarium.Infrastructure/Persistence/AppDbContext.cs` (add DbSet at line ~45; add entity config block after block "15. Schedule Configuration" at line ~403)
- Test: `Backend/Knotarium.Tests/Polling/PollingTriggerManifestTests.cs` (add a persistence test) — actually create new test class below.

- [ ] **Step 1: Create the shared test support helper**

`Backend/Knotarium.Tests/Polling/PollingTestSupport.cs`:

```csharp
using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Tests.Polling;

/// <summary>A TimeProvider whose "now" can be set explicitly in tests.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FixedTimeProvider(DateTimeOffset now) => _now = now;
    public void Set(DateTimeOffset now) => _now = now;
    public void Advance(TimeSpan by) => _now += by;
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>Creates an isolated SQLite-backed AppDbContext for a single test.</summary>
public static class PollingTestDb
{
    public static (SqliteConnection connection, DbContextOptions<AppDbContext> options) NewOptions()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return (connection, options);
    }
}
```

> NOTE: If an equivalent helper already exists in the Tests project (search for `EnsureCreated`), reuse it instead of adding this — DRY. The `FixedTimeProvider` is still worth adding if no `FakeTimeProvider` package is referenced.

- [ ] **Step 2: Write the failing persistence test**

`Backend/Knotarium.Tests/Polling/PollingTriggerPersistenceTests.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Knotarium.Tests.Polling;

public class PollingTriggerPersistenceTests
{
    [Fact]
    public async Task PollingTrigger_RoundTrips_WithCursorAndConfig()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var id = Guid.NewGuid();
            using (var write = new AppDbContext(options))
            {
                await write.Set<PollingTrigger>().AddAsync(new PollingTrigger
                {
                    Id = id,
                    WorkflowDefinitionId = new WorkflowDefinitionId("wf-1"),
                    IntervalSeconds = 60,
                    NextPollAtUtc = DateTimeOffset.UnixEpoch,
                    ConfigJson = "{\"sourceKind\":\"http\"}",
                    Cursor = "etag-123",
                    IsActive = true
                });
                await write.SaveChangesAsync();
            }

            using var read = new AppDbContext(options);
            var loaded = await read.Set<PollingTrigger>().SingleAsync(p => p.Id == id);
            Assert.Equal(60, loaded.IntervalSeconds);
            Assert.Equal("etag-123", loaded.Cursor);
            Assert.Equal("wf-1", loaded.WorkflowDefinitionId.Value);
            Assert.True(loaded.IsActive);
        }
        finally
        {
            connection.Dispose();
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollingTriggerPersistenceTests"`
Expected: FAIL — `PollingTrigger` type does not exist / `Set<PollingTrigger>()` has no mapped entity.

- [ ] **Step 4: Create the domain entity**

`Backend/Knotarium.Core/Domain/PollingTrigger.cs`:

```csharp
using System;

namespace Knotarium.Core.Domain;

/// <summary>
/// A persisted polling trigger derived from a pollingTrigger node. Mirrors <see cref="Schedule"/>
/// but adds change-detection cursor state and source configuration.
/// </summary>
public sealed class PollingTrigger
{
    public Guid Id { get; set; }
    public WorkflowDefinitionId WorkflowDefinitionId { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTimeOffset NextPollAtUtc { get; set; } // Tracked and evaluated in UTC
    public string ConfigJson { get; set; } = null!;   // sourceKind + change-detection + source fields
    public string? Cursor { get; set; }               // opaque last-seen state (etag/hash/json value)
    public bool IsActive { get; set; }
    public DateTimeOffset? LastPolledAtUtc { get; set; }
    public string? LastError { get; set; }
}
```

- [ ] **Step 5: Add the DbSet and entity config**

In `AppDbContext.cs`, add after line 42 (`public DbSet<Schedule> Schedules ...`):

```csharp
    public DbSet<PollingTrigger> PollingTriggers { get; set; } = null!;
```

Add a new configuration block immediately after the "15. Schedule Configuration" block (after line 403):

```csharp
        // 15b. PollingTrigger Configuration
        modelBuilder.Entity<PollingTrigger>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.WorkflowDefinitionId).HasConversion(workflowIdConverter).IsRequired();
            entity.Property(p => p.ConfigJson).IsRequired();
        });
```

> NOTE: `workflowIdConverter` is already declared at the top of `OnModelCreating` (line 56) and is in scope here. The global `DateTimeOffsetToBinaryConverter` loop at line 441 will pick up `NextPollAtUtc` and `LastPolledAtUtc` automatically.

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollingTriggerPersistenceTests"`
Expected: PASS.

- [ ] **Step 7: Add EF Core migration**

The app applies migrations at startup (see `Program.cs` near line 303). Generate one so the table exists for real (non-test) databases:

Run:
```bash
dotnet ef migrations add AddPollingTriggers \
  --project Backend/Knotarium.Infrastructure/Knotarium.Infrastructure.csproj \
  --startup-project Backend/Knotarium.Api/Knotarium.Api.csproj
```
Expected: a new migration file under `Backend/Knotarium.Infrastructure/Migrations/` creating the `PollingTriggers` table.

> NOTE: If the project does not use generated migrations (check whether a `Migrations/` folder exists; the codebase has manual `ALTER TABLE` guards in `Program.cs` ~line 303 for `IsEnabled`), then instead of `dotnet ef`, add a manual `CREATE TABLE IF NOT EXISTS PollingTriggers (...)` guard alongside the existing startup schema guards, matching that file's existing style. Determine which approach the repo uses before this step.

- [ ] **Step 8: Commit**

```bash
git add Backend/Knotarium.Core/Domain/PollingTrigger.cs \
        Backend/Knotarium.Infrastructure/Persistence/AppDbContext.cs \
        Backend/Knotarium.Infrastructure/Migrations \
        Backend/Knotarium.Tests/Polling/PollingTestSupport.cs \
        Backend/Knotarium.Tests/Polling/PollingTriggerPersistenceTests.cs
git commit -m "feat(polling): add PollingTrigger entity, EF mapping, and migration"
```

---

## Task 3: Source contracts + body change-detection

**Files:**
- Create: `Backend/Knotarium.Core/Contracts/IPollSource.cs`
- Create: `Backend/Knotarium.Features/Polling/BodyChangeDetector.cs`
- Test: `Backend/Knotarium.Tests/Polling/BodyChangeDetectorTests.cs`

`BodyChangeDetector` owns the transport-agnostic strategies (`hash`, `json-cursor`, `always`) over a response body string. Transport-level strategies (`etag`, `last-modified`) are handled inside each source because they need request headers. Defining the contracts first lets every later task compile against stable types.

- [ ] **Step 1: Create the contracts (no test needed — pure type declarations)**

`Backend/Knotarium.Core/Contracts/IPollSource.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>How a poll decides whether the source has new data since the last poll.</summary>
public enum PollChangeDetection
{
    Etag,
    LastModified,
    Hash,
    JsonCursor,
    Always
}

/// <summary>Inputs to a single poll: the source-specific config and the opaque prior cursor.</summary>
public sealed record PollContext(string ConfigJson, string? Cursor);

/// <summary>
/// Outcome of a single poll. <see cref="HasNew"/> drives whether a run is started;
/// <see cref="NewCursor"/> replaces the stored cursor when (and only when) new data is observed.
/// </summary>
public sealed record PollResult(bool HasNew, object? Payload, string? NewCursor);

/// <summary>A pluggable poll source. Implementations are resolved by <see cref="Kind"/>.</summary>
public interface IPollSource
{
    string Kind { get; }
    Task<PollResult> PollAsync(PollContext context, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the failing test for body change detection**

`Backend/Knotarium.Tests/Polling/BodyChangeDetectorTests.cs`:

```csharp
using Knotarium.Core.Contracts;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

public class BodyChangeDetectorTests
{
    [Fact]
    public void Hash_SameBody_NotNew()
    {
        var first = BodyChangeDetector.Detect(PollChangeDetection.Hash, "{\"a\":1}", cursor: null, jsonPath: null);
        var second = BodyChangeDetector.Detect(PollChangeDetection.Hash, "{\"a\":1}", cursor: first.NewCursor, jsonPath: null);

        Assert.True(first.HasNew);            // no prior cursor => new
        Assert.False(second.HasNew);          // identical body => unchanged
        Assert.Equal(first.NewCursor, second.NewCursor);
    }

    [Fact]
    public void Hash_ChangedBody_IsNew()
    {
        var first = BodyChangeDetector.Detect(PollChangeDetection.Hash, "{\"a\":1}", cursor: null, jsonPath: null);
        var second = BodyChangeDetector.Detect(PollChangeDetection.Hash, "{\"a\":2}", cursor: first.NewCursor, jsonPath: null);

        Assert.True(second.HasNew);
        Assert.NotEqual(first.NewCursor, second.NewCursor);
    }

    [Fact]
    public void JsonCursor_AdvancesOnLargerValue()
    {
        var first = BodyChangeDetector.Detect(PollChangeDetection.JsonCursor, "{\"id\":10}", cursor: null, jsonPath: "id");
        var same = BodyChangeDetector.Detect(PollChangeDetection.JsonCursor, "{\"id\":10}", cursor: first.NewCursor, jsonPath: "id");
        var advanced = BodyChangeDetector.Detect(PollChangeDetection.JsonCursor, "{\"id\":11}", cursor: first.NewCursor, jsonPath: "id");

        Assert.True(first.HasNew);
        Assert.Equal("10", first.NewCursor);
        Assert.False(same.HasNew);
        Assert.True(advanced.HasNew);
        Assert.Equal("11", advanced.NewCursor);
    }

    [Fact]
    public void JsonCursor_NestedPath()
    {
        var result = BodyChangeDetector.Detect(
            PollChangeDetection.JsonCursor, "{\"meta\":{\"latest\":\"2026-06-14\"}}", cursor: null, jsonPath: "meta.latest");
        Assert.True(result.HasNew);
        Assert.Equal("2026-06-14", result.NewCursor);
    }

    [Fact]
    public void Always_IsAlwaysNew()
    {
        var result = BodyChangeDetector.Detect(PollChangeDetection.Always, "anything", cursor: "anything", jsonPath: null);
        Assert.True(result.HasNew);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~BodyChangeDetectorTests"`
Expected: FAIL — `BodyChangeDetector` does not exist.

- [ ] **Step 4: Implement `BodyChangeDetector`**

`Backend/Knotarium.Features/Polling/BodyChangeDetector.cs`:

```csharp
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>
/// Transport-agnostic change detection over a response body. Handles Hash, JsonCursor and Always.
/// Etag / LastModified are transport-level and handled by the source itself.
/// </summary>
public static class BodyChangeDetector
{
    public static PollResult Detect(PollChangeDetection strategy, string body, string? cursor, string? jsonPath)
    {
        switch (strategy)
        {
            case PollChangeDetection.Always:
                return new PollResult(HasNew: true, Payload: body, NewCursor: cursor);

            case PollChangeDetection.Hash:
            {
                var hash = ComputeHash(body);
                var hasNew = !string.Equals(hash, cursor, StringComparison.Ordinal);
                return new PollResult(hasNew, Payload: hasNew ? body : null, NewCursor: hash);
            }

            case PollChangeDetection.JsonCursor:
            {
                var value = ExtractJsonValue(body, jsonPath);
                if (value is null)
                {
                    // Path missing: treat as no change so a malformed/empty response never floods runs.
                    return new PollResult(HasNew: false, Payload: null, NewCursor: cursor);
                }

                var hasNew = IsAdvanced(value, cursor);
                return new PollResult(hasNew, Payload: hasNew ? body : null, NewCursor: hasNew ? value : cursor);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy,
                    "BodyChangeDetector only handles Hash, JsonCursor and Always.");
        }
    }

    private static string ComputeHash(string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(bytes);
    }

    private static string? ExtractJsonValue(string body, string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var element = doc.RootElement;
            foreach (var segment in jsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty(segment, out var next))
                {
                    return null;
                }

                element = next;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                _ => element.GetRawText()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAdvanced(string value, string? cursor)
    {
        if (cursor is null)
        {
            return true;
        }

        if (string.Equals(value, cursor, StringComparison.Ordinal))
        {
            return false;
        }

        // Numeric cursors must strictly increase; non-numeric cursors are "new" on any difference.
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var newNum) &&
            double.TryParse(cursor, NumberStyles.Any, CultureInfo.InvariantCulture, out var oldNum))
        {
            return newNum > oldNum;
        }

        return true;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~BodyChangeDetectorTests"`
Expected: PASS (all 5).

- [ ] **Step 6: Commit**

```bash
git add Backend/Knotarium.Core/Contracts/IPollSource.cs \
        Backend/Knotarium.Features/Polling/BodyChangeDetector.cs \
        Backend/Knotarium.Tests/Polling/BodyChangeDetectorTests.cs
git commit -m "feat(polling): add IPollSource contracts and body change detector"
```

---

## Task 4: `HttpPollSource` (Source A)

**Files:**
- Create: `Backend/Knotarium.Features/Polling/HttpPollSource.cs`
- Test: `Backend/Knotarium.Tests/Polling/HttpPollSourceTests.cs`

Mirrors `HttpRequestNodeTask` for client/credential handling. Owns `etag`/`last-modified` (conditional request + `304`) and delegates `hash`/`json-cursor`/`always` to `BodyChangeDetector`.

- [ ] **Step 1: Write the failing test (fake handler)**

`Backend/Knotarium.Tests/Polling/HttpPollSourceTests.cs`:

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

public class HttpPollSourceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private sealed class NullSecretResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static HttpPollSource CreateSource(HttpMessageHandler handler) =>
        new HttpPollSource(new StubFactory(handler), new NullSecretResolver());

    [Fact]
    public async Task Etag_304_ReportsNoNew()
    {
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.NotModified);
            return resp;
        });
        var source = CreateSource(handler);
        var config = "{\"changeDetection\":\"etag\",\"url\":\"https://x.test/feed\",\"method\":\"GET\"}";

        var result = await source.PollAsync(new PollContext(config, Cursor: "\"abc\""), CancellationToken.None);

        Assert.False(result.HasNew);
        Assert.Equal("\"abc\"", handler.LastRequest!.Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task Etag_200_ReportsNewAndStoresEtag()
    {
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"v\":1}") };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new-etag\"");
            return resp;
        });
        var source = CreateSource(handler);
        var config = "{\"changeDetection\":\"etag\",\"url\":\"https://x.test/feed\",\"method\":\"GET\"}";

        var result = await source.PollAsync(new PollContext(config, Cursor: null), CancellationToken.None);

        Assert.True(result.HasNew);
        Assert.Equal("\"new-etag\"", result.NewCursor);
        Assert.Equal("{\"v\":1}", result.Payload);
    }

    [Fact]
    public async Task Hash_DelegatesToBodyDetector()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"v\":1}") });
        var source = CreateSource(handler);
        var config = "{\"changeDetection\":\"hash\",\"url\":\"https://x.test/feed\",\"method\":\"GET\"}";

        var first = await source.PollAsync(new PollContext(config, Cursor: null), CancellationToken.None);
        var second = await source.PollAsync(new PollContext(config, Cursor: first.NewCursor), CancellationToken.None);

        Assert.True(first.HasNew);
        Assert.False(second.HasNew);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~HttpPollSourceTests"`
Expected: FAIL — `HttpPollSource` does not exist.

- [ ] **Step 3: Implement `HttpPollSource`**

`Backend/Knotarium.Features/Polling/HttpPollSource.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>Polls an arbitrary HTTP endpoint. Mirrors HttpRequestNodeTask for client/credential handling.</summary>
public sealed class HttpPollSource : IPollSource
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ISecretResolver _secretResolver;

    public HttpPollSource(IHttpClientFactory clientFactory, ISecretResolver secretResolver)
    {
        _clientFactory = clientFactory;
        _secretResolver = secretResolver;
    }

    public string Kind => "http";

    public async Task<PollResult> PollAsync(PollContext context, CancellationToken cancellationToken)
    {
        using var configDoc = JsonDocument.Parse(context.ConfigJson);
        var root = configDoc.RootElement;

        var url = GetString(root, "url") ?? throw new InvalidOperationException("Polling HTTP source is missing 'url'.");
        var method = GetString(root, "method") ?? "GET";
        var strategy = ParseStrategy(GetString(root, "changeDetection"));
        var jsonPath = GetString(root, "jsonCursorPath");

        var client = _clientFactory.CreateClient("HttpNode");
        var request = new HttpRequestMessage(new HttpMethod(method), url);

        ApplyHeaders(request, GetString(root, "headersJson"));
        await ApplyCredentialAsync(request, GetString(root, "apiKeySecretRef"), cancellationToken);
        ApplyConditionalHeaders(request, strategy, context.Cursor);

        var response = await client.SendAsync(request, cancellationToken);

        if ((strategy == PollChangeDetection.Etag || strategy == PollChangeDetection.LastModified)
            && response.StatusCode == HttpStatusCode.NotModified)
        {
            return new PollResult(HasNew: false, Payload: null, NewCursor: context.Cursor);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return strategy switch
        {
            PollChangeDetection.Etag => FromValidator(response.Headers.ETag?.Tag, context.Cursor, body),
            PollChangeDetection.LastModified => FromValidator(
                response.Content.Headers.LastModified?.ToString("O", CultureInfo.InvariantCulture), context.Cursor, body),
            _ => BodyChangeDetector.Detect(strategy, body, context.Cursor, jsonPath)
        };
    }

    private static PollResult FromValidator(string? validator, string? cursor, string body)
    {
        // No validator header present: fall back to "always new" so the run is not silently skipped.
        if (string.IsNullOrEmpty(validator))
        {
            return new PollResult(HasNew: true, Payload: body, NewCursor: cursor);
        }

        var hasNew = !string.Equals(validator, cursor, StringComparison.Ordinal);
        return new PollResult(hasNew, Payload: hasNew ? body : null, NewCursor: validator);
    }

    private static void ApplyConditionalHeaders(HttpRequestMessage request, PollChangeDetection strategy, string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return;
        }

        if (strategy == PollChangeDetection.Etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cursor);
        }
        else if (strategy == PollChangeDetection.LastModified)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", cursor);
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return;
        }

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private async Task ApplyCredentialAsync(HttpRequestMessage request, string? secretRef, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secretRef))
        {
            return;
        }

        var secret = await _secretResolver.ResolveAsync(secretRef, ct);
        if (!string.IsNullOrEmpty(secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    private static PollChangeDetection ParseStrategy(string? raw) => raw switch
    {
        "etag" => PollChangeDetection.Etag,
        "last-modified" => PollChangeDetection.LastModified,
        "hash" => PollChangeDetection.Hash,
        "json-cursor" => PollChangeDetection.JsonCursor,
        "always" => PollChangeDetection.Always,
        _ => PollChangeDetection.Hash
    };

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~HttpPollSourceTests"`
Expected: PASS (all 3).

- [ ] **Step 5: Commit**

```bash
git add Backend/Knotarium.Features/Polling/HttpPollSource.cs \
        Backend/Knotarium.Tests/Polling/HttpPollSourceTests.cs
git commit -m "feat(polling): add HttpPollSource with etag/last-modified/hash/json-cursor/always"
```

---

## Task 5: Source registry

**Files:**
- Create: `Backend/Knotarium.Features/Polling/PollSourceRegistry.cs`
- Test: `Backend/Knotarium.Tests/Polling/PollSourceRegistryTests.cs`

Resolves an `IPollSource` by `Kind` from the set registered in DI.

- [ ] **Step 1: Write the failing test**

`Backend/Knotarium.Tests/Polling/PollSourceRegistryTests.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

public class PollSourceRegistryTests
{
    private sealed class FakeSource : IPollSource
    {
        public FakeSource(string kind) => Kind = kind;
        public string Kind { get; }
        public Task<PollResult> PollAsync(PollContext c, CancellationToken ct) =>
            Task.FromResult(new PollResult(false, null, null));
    }

    [Fact]
    public void Resolve_ReturnsMatchingSource_CaseInsensitive()
    {
        var registry = new PollSourceRegistry(new IPollSource[] { new FakeSource("http"), new FakeSource("openapi") });
        Assert.Equal("openapi", registry.Resolve("OpenApi").Kind);
    }

    [Fact]
    public void Resolve_UnknownKind_Throws()
    {
        var registry = new PollSourceRegistry(new IPollSource[] { new FakeSource("http") });
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("ftp"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollSourceRegistryTests"`
Expected: FAIL — `PollSourceRegistry` does not exist.

- [ ] **Step 3: Implement `PollSourceRegistry`**

`Backend/Knotarium.Features/Polling/PollSourceRegistry.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>Resolves a registered <see cref="IPollSource"/> by its kind.</summary>
public sealed class PollSourceRegistry
{
    private readonly Dictionary<string, IPollSource> _sources;

    public PollSourceRegistry(IEnumerable<IPollSource> sources)
    {
        _sources = sources.ToDictionary(s => s.Kind, StringComparer.OrdinalIgnoreCase);
    }

    public IPollSource Resolve(string kind)
    {
        if (_sources.TryGetValue(kind, out var source))
        {
            return source;
        }

        throw new InvalidOperationException($"No poll source registered for kind '{kind}'.");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollSourceRegistryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Backend/Knotarium.Features/Polling/PollSourceRegistry.cs \
        Backend/Knotarium.Tests/Polling/PollSourceRegistryTests.cs
git commit -m "feat(polling): add PollSourceRegistry"
```

---

## Task 6: `WorkflowPollingTriggerSynchronizer`

**Files:**
- Create: `Backend/Knotarium.Api/Services/WorkflowPollingTriggerIdFactory.cs`
- Create: `Backend/Knotarium.Api/Services/WorkflowPollingTriggerSynchronizer.cs`
- Test: `Backend/Knotarium.Tests/Polling/WorkflowPollingTriggerSynchronizerTests.cs`

Reconciles `PollingTrigger` rows from `pollingTrigger` nodes on workflow save. Preserves cursor on benign edits; resets it when the source identity changes.

- [ ] **Step 1: Create the id factory (no separate test — exercised via synchronizer)**

`Backend/Knotarium.Api/Services/WorkflowPollingTriggerIdFactory.cs`:

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using Knotarium.Core.Domain;

namespace Knotarium.Api.Services;

/// <summary>Creates stable polling-trigger identifiers for pollingTrigger nodes within a workflow.</summary>
internal static class WorkflowPollingTriggerIdFactory
{
    public static Guid Create(WorkflowDefinitionId workflowId, NodeId nodeId)
    {
        // Distinct namespace prefix from schedules so a scheduler and a pollingTrigger sharing a node id never collide.
        var keyBytes = Encoding.UTF8.GetBytes($"poll:{workflowId.Value}:{nodeId.Value}");
        var hash = MD5.HashData(keyBytes);
        return new Guid(hash);
    }
}
```

- [ ] **Step 2: Write the failing test**

`Backend/Knotarium.Tests/Polling/WorkflowPollingTriggerSynchronizerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Api.Services;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Knotarium.Tests.Polling;

public class WorkflowPollingTriggerSynchronizerTests
{
    private static WorkflowDefinition WorkflowWithPollNode(string url, int interval = 60)
    {
        var node = new NodeDefinition
        {
            Id = NodeId.Create("poll-1"),
            Type = "pollingTrigger",
            Properties = new Dictionary<string, object>
            {
                ["intervalSeconds"] = interval,
                ["sourceKind"] = "http",
                ["changeDetection"] = "hash",
                ["url"] = url
            }
        };
        return new WorkflowDefinition
        {
            Id = new WorkflowDefinitionId("wf-1"),
            Name = "wf",
            Nodes = new List<NodeDefinition> { node },
            Edges = new List<EdgeDefinition>()
        };
    }

    [Fact]
    public async Task Sync_CreatesRow_ForPollNode()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a"), CancellationToken.None);

            var row = await db.Set<PollingTrigger>().SingleAsync();
            Assert.Equal(60, row.IntervalSeconds);
            Assert.True(row.IsActive);
            Assert.Equal(DateTimeOffset.UnixEpoch, row.NextPollAtUtc); // poll promptly on first arm
            Assert.Contains("https://x.test/a", row.ConfigJson);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Sync_PreservesCursor_OnBenignEdit()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a", interval: 60), CancellationToken.None);
            var row = await db.Set<PollingTrigger>().SingleAsync();
            row.Cursor = "saved-cursor";
            await db.SaveChangesAsync();

            // Same url, different interval => benign edit, cursor kept.
            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a", interval: 120), CancellationToken.None);

            var updated = await db.Set<PollingTrigger>().SingleAsync();
            Assert.Equal(120, updated.IntervalSeconds);
            Assert.Equal("saved-cursor", updated.Cursor);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Sync_ResetsCursor_WhenSourceIdentityChanges()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a"), CancellationToken.None);
            var row = await db.Set<PollingTrigger>().SingleAsync();
            row.Cursor = "saved-cursor";
            await db.SaveChangesAsync();

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/DIFFERENT"), CancellationToken.None);

            var updated = await db.Set<PollingTrigger>().SingleAsync();
            Assert.Null(updated.Cursor);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Sync_RemovesRows_WhenNodeDeleted()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a"), CancellationToken.None);
            var emptyWorkflow = new WorkflowDefinition
            {
                Id = new WorkflowDefinitionId("wf-1"),
                Name = "wf",
                Nodes = new List<NodeDefinition>(),
                Edges = new List<EdgeDefinition>()
            };

            await sync.SyncAsync(emptyWorkflow, CancellationToken.None);

            Assert.Empty(await db.Set<PollingTrigger>().ToListAsync());
        }
        finally { connection.Dispose(); }
    }
}
```

> NOTE: `NodeDefinition` / `EdgeDefinition` / `WorkflowDefinition` construction must match their real definitions (they may be records with positional ctors, or have an `Id` of type `NodeId`). Read `Backend/Knotarium.Core/Domain/WorkflowDefinition.cs` and `NodeDefinition.cs` and adjust the object initializers to compile. The synchronizer logic does not depend on the exact construction style.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~WorkflowPollingTriggerSynchronizerTests"`
Expected: FAIL — `WorkflowPollingTriggerSynchronizer` does not exist.

- [ ] **Step 4: Implement the synchronizer**

`Backend/Knotarium.Api/Services/WorkflowPollingTriggerSynchronizer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Api.Services;

/// <summary>
/// Synchronizes persisted PollingTrigger rows from pollingTrigger nodes in a workflow definition.
/// Cursor is preserved across benign edits and reset when the source identity changes.
/// </summary>
internal sealed class WorkflowPollingTriggerSynchronizer
{
    private static readonly string[] SourceIdentityKeys =
        { "sourceKind", "url", "serverConfigId", "operationId", "specVersion" };

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public WorkflowPollingTriggerSynchronizer(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task SyncAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var existing = await _dbContext.PollingTriggers
            .Where(p => p.WorkflowDefinitionId == workflow.Id)
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (var node in workflow.Nodes.Where(n => n.Type.Equals("pollingTrigger", StringComparison.OrdinalIgnoreCase)))
        {
            var intervalSeconds = GetIntervalSeconds(node);
            var configJson = BuildConfigJson(node);
            var id = WorkflowPollingTriggerIdFactory.Create(workflow.Id, node.Id);

            if (existing.Remove(id, out var row))
            {
                if (SourceIdentityChanged(row.ConfigJson, configJson))
                {
                    row.Cursor = null;
                }

                row.IntervalSeconds = intervalSeconds;
                row.ConfigJson = configJson;
                row.IsActive = true;
                continue;
            }

            await _dbContext.PollingTriggers.AddAsync(new PollingTrigger
            {
                Id = id,
                WorkflowDefinitionId = workflow.Id,
                IntervalSeconds = intervalSeconds,
                NextPollAtUtc = _timeProvider.GetUtcNow(), // poll promptly once armed
                ConfigJson = configJson,
                Cursor = null,
                IsActive = true
            }, cancellationToken);
        }

        foreach (var obsolete in existing.Values)
        {
            _dbContext.PollingTriggers.Remove(obsolete);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static int GetIntervalSeconds(NodeDefinition node)
    {
        if (!node.Properties.TryGetValue("intervalSeconds", out var raw) || raw is null ||
            !int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0)
        {
            throw new InvalidOperationException(
                $"pollingTrigger node '{node.Id.Value}' has a missing or invalid 'intervalSeconds'.");
        }

        return seconds;
    }

    private static string BuildConfigJson(NodeDefinition node)
    {
        // Persist the node's configuration verbatim (string-valued) so the source can read it back.
        var config = new Dictionary<string, string>();
        foreach (var key in new[]
                 {
                     "sourceKind", "changeDetection", "jsonCursorPath",
                     "url", "method", "headersJson", "apiKeySecretRef",
                     "serverConfigId", "operationId", "specVersion"
                 })
        {
            if (node.Properties.TryGetValue(key, out var value) && value is not null)
            {
                config[key] = value.ToString()!;
            }
        }

        return JsonSerializer.Serialize(config);
    }

    private static bool SourceIdentityChanged(string oldConfigJson, string newConfigJson)
    {
        var oldConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(oldConfigJson) ?? new();
        var newConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(newConfigJson) ?? new();

        foreach (var key in SourceIdentityKeys)
        {
            oldConfig.TryGetValue(key, out var oldValue);
            newConfig.TryGetValue(key, out var newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~WorkflowPollingTriggerSynchronizerTests"`
Expected: PASS (all 4).

- [ ] **Step 6: Commit**

```bash
git add Backend/Knotarium.Api/Services/WorkflowPollingTriggerIdFactory.cs \
        Backend/Knotarium.Api/Services/WorkflowPollingTriggerSynchronizer.cs \
        Backend/Knotarium.Tests/Polling/WorkflowPollingTriggerSynchronizerTests.cs
git commit -m "feat(polling): reconcile PollingTrigger rows on workflow save"
```

---

## Task 7: `PollEvaluationService`

**Files:**
- Create: `Backend/Knotarium.Core/Contracts/IPollEvaluationService.cs`
- Create: `Backend/Knotarium.Features/Polling/PollEvaluationService.cs`
- Test: `Backend/Knotarium.Tests/Polling/PollEvaluationServiceTests.cs`

Selects due, active triggers whose workflow is enabled; polls each via the registry; enqueues an `ExecutionInstance` only when `HasNew`; always advances `NextPollAtUtc` and records diagnostics — all per trigger in one transaction.

- [ ] **Step 1: Create the interface**

`Backend/Knotarium.Core/Contracts/IPollEvaluationService.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>Evaluates active polling triggers that are due and conditionally enqueues runs.</summary>
public interface IPollEvaluationService
{
    Task EvaluateDuePollsAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Write the failing test**

`Backend/Knotarium.Tests/Polling/PollEvaluationServiceTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Knotarium.Features.Polling;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knotarium.Tests.Polling;

public class PollEvaluationServiceTests
{
    private sealed class ScriptedSource : IPollSource
    {
        private readonly PollResult _result;
        public ScriptedSource(PollResult result) => _result = result;
        public string Kind => "http";
        public int Calls { get; private set; }
        public Task<PollResult> PollAsync(PollContext c, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private static async Task SeedWorkflowAsync(AppDbContext db, bool enabled)
    {
        await db.WorkflowDefinitions.AddAsync(new WorkflowDefinition
        {
            Id = new WorkflowDefinitionId("wf-1"),
            Name = "wf",
            Nodes = new List<NodeDefinition>(),
            Edges = new List<EdgeDefinition>(),
            IsEnabled = enabled
        });
        // An active version is required for enqueue; reuse the production helper or seed directly.
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedTriggerAsync(AppDbContext db, DateTimeOffset nextPoll)
    {
        var id = Guid.NewGuid();
        await db.PollingTriggers.AddAsync(new PollingTrigger
        {
            Id = id,
            WorkflowDefinitionId = new WorkflowDefinitionId("wf-1"),
            IntervalSeconds = 60,
            NextPollAtUtc = nextPoll,
            ConfigJson = "{\"sourceKind\":\"http\",\"changeDetection\":\"hash\",\"url\":\"https://x.test/a\"}",
            Cursor = null,
            IsActive = true
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static PollEvaluationService CreateService(
        DbContextOptions<AppDbContext> options, FixedTimeProvider time, IPollSource source, IWorkflowEnqueueService enqueue)
    {
        var db = new AppDbContext(options);
        var registry = new PollSourceRegistry(new[] { source });
        return new PollEvaluationService(db, registry, enqueue, time, NullLogger<PollEvaluationService>.Instance);
    }

    [Fact]
    public async Task HasNew_True_EnqueuesAndAdvances_AndStoresCursor()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowAsync(seed, enabled: true);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var source = new ScriptedSource(new PollResult(HasNew: true, Payload: "{\"v\":1}", NewCursor: "cur-1"));
            var enqueue = new RecordingEnqueueService();
            var service = CreateService(options, time, source, enqueue);

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            using var verify = new AppDbContext(options);
            var row = await verify.PollingTriggers.SingleAsync();
            Assert.Equal("cur-1", row.Cursor);
            Assert.Equal(time.GetUtcNow().AddSeconds(60), row.NextPollAtUtc);
            Assert.Equal(1, enqueue.EnqueueCount);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task HasNew_False_NoEnqueue_StillAdvances()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowAsync(seed, enabled: true);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var source = new ScriptedSource(new PollResult(HasNew: false, Payload: null, NewCursor: null));
            var enqueue = new RecordingEnqueueService();
            var service = CreateService(options, time, source, enqueue);

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            using var verify = new AppDbContext(options);
            var row = await verify.PollingTriggers.SingleAsync();
            Assert.Equal(time.GetUtcNow().AddSeconds(60), row.NextPollAtUtc);
            Assert.Equal(0, enqueue.EnqueueCount);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task DisabledWorkflow_NotPolled()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowAsync(seed, enabled: false);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var source = new ScriptedSource(new PollResult(true, "x", "cur"));
            var enqueue = new RecordingEnqueueService();
            var service = CreateService(options, time, source, enqueue);

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            Assert.Equal(0, source.Calls);
            Assert.Equal(0, enqueue.EnqueueCount);
        }
        finally { connection.Dispose(); }
    }
}
```

> NOTE: `RecordingEnqueueService` is a test double for whatever enqueue abstraction this service uses. See Step 3 — the service depends on a small `IPollRunEnqueuer` (defined below) rather than the schedule-specific `IWorkflowEnqueueService`, because polling needs to attach a payload. Implement `RecordingEnqueueService : IPollRunEnqueuer` in this test file:
> ```csharp
> private sealed class RecordingEnqueueService : IPollRunEnqueuer
> {
>     public int EnqueueCount { get; private set; }
>     public Task EnqueueAsync(WorkflowDefinitionId workflowId, object? payload, CancellationToken ct)
>     { EnqueueCount++; return Task.CompletedTask; }
> }
> ```
> Remove the unused `IWorkflowEnqueueService`/`Features.Execution` usings if not needed after this change.

- [ ] **Step 3: Create the run enqueuer abstraction + implementation**

Polling needs to create an `ExecutionInstance` with `TriggerOrigin = "poll"` and the payload in `GlobalVariables`. Add a focused abstraction so the evaluation service stays testable.

`Backend/Knotarium.Core/Contracts/IPollRunEnqueuer.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>Creates and queues a workflow run started by a polling trigger.</summary>
public interface IPollRunEnqueuer
{
    /// <summary>Returns true if a run was created (false when the workflow has no active version).</summary>
    Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, object? payload, CancellationToken cancellationToken);
}
```

`Backend/Knotarium.Features/Polling/PollRunEnqueuer.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Features.Polling;

/// <summary>Default enqueuer: creates an ExecutionInstance carrying the polled payload and queues it.</summary>
public sealed class PollRunEnqueuer : IPollRunEnqueuer
{
    public const string PayloadVariableKey = "__pollPayload";

    private readonly AppDbContext _dbContext;
    private readonly WorkflowExecutionQueue _queue;
    private readonly ActiveWorkflowVersionService _activeWorkflowVersionService;
    private readonly TimeProvider _timeProvider;

    public PollRunEnqueuer(
        AppDbContext dbContext,
        WorkflowExecutionQueue queue,
        ActiveWorkflowVersionService activeWorkflowVersionService,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _queue = queue;
        _activeWorkflowVersionService = activeWorkflowVersionService;
        _timeProvider = timeProvider;
    }

    public async Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, object? payload, CancellationToken cancellationToken)
    {
        var version = await _activeWorkflowVersionService.GetActiveVersionAsync(workflowId, cancellationToken);
        if (version is null)
        {
            return false;
        }

        var globals = new Dictionary<string, object>();
        if (payload is not null)
        {
            globals[PayloadVariableKey] = payload;
        }

        var execution = new ExecutionInstance
        {
            Id = ExecutionInstanceId.New(),
            WorkflowDefinitionId = workflowId,
            WorkflowVersionId = version.Id,
            Status = ExecutionStatus.Pending,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
            TriggerOrigin = "poll",
            GlobalVariables = globals
        };

        await _dbContext.ExecutionInstances.AddAsync(execution, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _queue.QueueExecution(execution.Id);
        return true;
    }
}
```

> NOTE: Confirm `ActiveWorkflowVersionService.GetActiveVersionAsync(WorkflowDefinitionId, ct)` and `WorkflowExecutionQueue.QueueExecution(ExecutionInstanceId)` signatures against `WorkflowEnqueueService.cs` (lines 85, 123) — they are used there identically.

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollEvaluationServiceTests"`
Expected: FAIL — `PollEvaluationService` does not exist.

- [ ] **Step 5: Implement `PollEvaluationService`**

`Backend/Knotarium.Features/Polling/PollEvaluationService.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knotarium.Features.Polling;

/// <summary>Evaluates due polling triggers and conditionally enqueues runs.</summary>
public sealed partial class PollEvaluationService : IPollEvaluationService
{
    private readonly AppDbContext _dbContext;
    private readonly PollSourceRegistry _sourceRegistry;
    private readonly IPollRunEnqueuer _runEnqueuer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PollEvaluationService> _logger;

    public PollEvaluationService(
        AppDbContext dbContext,
        PollSourceRegistry sourceRegistry,
        IPollRunEnqueuer runEnqueuer,
        TimeProvider timeProvider,
        ILogger<PollEvaluationService> logger)
    {
        _dbContext = dbContext;
        _sourceRegistry = sourceRegistry;
        _runEnqueuer = runEnqueuer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task EvaluateDuePollsAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var disabledWorkflowIds = _dbContext.WorkflowDefinitions
            .Where(workflow => !workflow.IsEnabled)
            .Select(workflow => workflow.Id);

        var dueTriggers = await _dbContext.PollingTriggers
            .Where(trigger => trigger.IsActive
                && trigger.NextPollAtUtc <= now
                && !disabledWorkflowIds.Contains(trigger.WorkflowDefinitionId))
            .OrderBy(trigger => trigger.NextPollAtUtc)
            .ToListAsync(cancellationToken);

        foreach (var trigger in dueTriggers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessTriggerAsync(trigger, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log.PollEvaluationFailed(_logger, trigger.Id, exception);
                await RecordFailureAsync(trigger, now, exception.Message, cancellationToken);
            }
        }
    }

    private async Task ProcessTriggerAsync(PollingTrigger trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sourceKind = ReadSourceKind(trigger.ConfigJson);
        var source = _sourceRegistry.Resolve(sourceKind);

        var result = await source.PollAsync(new PollContext(trigger.ConfigJson, trigger.Cursor), cancellationToken);

        if (result.HasNew)
        {
            var created = await _runEnqueuer.EnqueueAsync(trigger.WorkflowDefinitionId, result.Payload, cancellationToken);
            if (created)
            {
                trigger.Cursor = result.NewCursor;
            }
            else
            {
                Log.MissingActiveVersionSkipped(_logger, trigger.Id, trigger.WorkflowDefinitionId.Value);
            }
        }

        trigger.NextPollAtUtc = now.AddSeconds(trigger.IntervalSeconds);
        trigger.LastPolledAtUtc = now;
        trigger.LastError = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(PollingTrigger trigger, DateTimeOffset now, string error, CancellationToken cancellationToken)
    {
        trigger.NextPollAtUtc = now.AddSeconds(trigger.IntervalSeconds); // advance even on failure: no hammering
        trigger.LastPolledAtUtc = now;
        trigger.LastError = error;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string ReadSourceKind(string configJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(configJson);
        return doc.RootElement.TryGetProperty("sourceKind", out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String
            ? prop.GetString()!
            : "http";
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1300, Level = LogLevel.Error, Message = "Failed to evaluate polling trigger {TriggerId}.")]
        public static partial void PollEvaluationFailed(ILogger logger, Guid triggerId, Exception exception);

        [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Polling trigger {TriggerId} skipped enqueue because workflow {WorkflowId} has no active version.")]
        public static partial void MissingActiveVersionSkipped(ILogger logger, Guid triggerId, string workflowId);
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollEvaluationServiceTests"`
Expected: PASS (all 3).

> NOTE: The `HasNew_True...` test asserts a real enqueue count via the `RecordingEnqueueService` double, so it does not need an active version seeded. The `DisabledWorkflow` test asserts the source is never called.

- [ ] **Step 7: Commit**

```bash
git add Backend/Knotarium.Core/Contracts/IPollEvaluationService.cs \
        Backend/Knotarium.Core/Contracts/IPollRunEnqueuer.cs \
        Backend/Knotarium.Features/Polling/PollRunEnqueuer.cs \
        Backend/Knotarium.Features/Polling/PollEvaluationService.cs \
        Backend/Knotarium.Tests/Polling/PollEvaluationServiceTests.cs
git commit -m "feat(polling): add poll evaluation service and run enqueuer"
```

---

## Task 8: `PollingWorker` + DI + save-endpoint wiring

**Files:**
- Create: `Backend/Knotarium.Api/Services/PollingWorker.cs`
- Modify: `Backend/Knotarium.Api/Program.cs` (DI registrations near line 115-154; save endpoints at lines 695 & 726)

No unit test for the worker loop itself (it is a thin timer like `SchedulingWorker`, which also has none); coverage comes from `PollEvaluationServiceTests`. This task is verified by build + the existing test suite staying green.

- [ ] **Step 1: Create the worker**

`Backend/Knotarium.Api/Services/PollingWorker.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;

namespace Knotarium.Api.Services;

/// <summary>Thin polling loop that delegates due polling-trigger evaluation to the Features layer.</summary>
public sealed partial class PollingWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _serviceProvider;
    private readonly RuntimeArmingState _armingState;
    private readonly ILogger<PollingWorker> _logger;

    public PollingWorker(IServiceProvider serviceProvider, RuntimeArmingState armingState, ILogger<PollingWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _armingState = armingState ?? throw new ArgumentNullException(nameof(armingState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var pollTimer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_armingState.IsArmed)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var evaluator = scope.ServiceProvider.GetRequiredService<IPollEvaluationService>();
                    await evaluator.EvaluateDuePollsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.UnhandledWorkerException(_logger, exception);
            }

            try
            {
                await pollTimer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1310, Level = LogLevel.Error, Message = "Polling worker encountered an unhandled exception.")]
        public static partial void UnhandledWorkerException(ILogger logger, Exception exception);
    }
}
```

- [ ] **Step 2: Register services in `Program.cs`**

After line 117 (`builder.Services.AddScoped<WorkflowScheduleSynchronizer>();`) add:

```csharp
builder.Services.AddScoped<WorkflowPollingTriggerSynchronizer>();
builder.Services.AddScoped<IPollEvaluationService, Knotarium.Features.Polling.PollEvaluationService>();
builder.Services.AddScoped<Knotarium.Core.Contracts.IPollRunEnqueuer, Knotarium.Features.Polling.PollRunEnqueuer>();
builder.Services.AddSingleton<Knotarium.Features.Polling.PollSourceRegistry>();
builder.Services.AddSingleton<Knotarium.Core.Contracts.IPollSource, Knotarium.Features.Polling.HttpPollSource>();
```

After line 144 (`builder.Services.AddHostedService<SchedulingWorker>();`) add:

```csharp
builder.Services.AddHostedService<PollingWorker>();
```

> NOTE: `PollSourceRegistry`'s constructor takes `IEnumerable<IPollSource>`, which the container supplies automatically from all registered `IPollSource` singletons. `HttpPollSource` depends on `IHttpClientFactory` (already registered — `HttpRequestNodeTask` uses it) and `ISecretResolver` (confirm it is registered; search `Program.cs` for `ISecretResolver` and add `AddScoped`/`AddSingleton` if missing — but it is already used by `HttpRequestNodeTask` at runtime, so it should be). If `ISecretResolver` is scoped, register `HttpPollSource` as scoped and `PollSourceRegistry` as scoped too, to avoid a captive-dependency error. Prefer scoped for both if unsure.

- [ ] **Step 3: Wire the synchronizer into both save endpoints**

In the `MapPost("/api/workflows", ...)` handler (line 695), add the new parameter and call. Change the lambda signature to include `WorkflowPollingTriggerSynchronizer pollingSynchronizer` and, right after `await scheduleSynchronizer.SyncAsync(persistedWorkflow);` (line 711), add:

```csharp
        await pollingSynchronizer.SyncAsync(persistedWorkflow);
```

In the `MapPut("/api/workflows/{id}", ...)` handler (line 726), add the same parameter and, right after `await scheduleSynchronizer.SyncAsync(updatedWorkflow);` (line 756), add:

```csharp
        await pollingSynchronizer.SyncAsync(updatedWorkflow);
```

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build Backend/Knotarium.sln` then `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj`
Expected: build succeeds; all tests pass (no regressions).

> NOTE: If a captive-dependency `InvalidOperationException` is thrown at startup (singleton consuming scoped), make `PollSourceRegistry`, `HttpPollSource`, and the `IPollSource` registrations `AddScoped` instead of `AddSingleton`, and resolve the registry from the per-tick scope (it already is — the worker creates a scope each tick).

- [ ] **Step 5: Commit**

```bash
git add Backend/Knotarium.Api/Services/PollingWorker.cs Backend/Knotarium.Api/Program.cs
git commit -m "feat(polling): add PollingWorker, DI registrations, and save-endpoint sync"
```

---

## Task 9: Executor wiring (payload on `result`, origin mapping)

**Files:**
- Modify: `Backend/Knotarium.Features/Execution/WorkflowExecutor.cs` (`CreateTriggerOutputs` ~line 1666; `IsTriggerCompatibleWithOrigin` ~line 1677)
- Test: `Backend/Knotarium.Tests/Polling/PollingTriggerExecutorTests.cs`

When a run is started by a poll (`TriggerOrigin = "poll"`), the entry node must resolve to the `pollingTrigger` node and that node must emit the polled payload on its `result` port.

- [ ] **Step 1: Write the failing unit test for the two helpers**

These are private methods; test them through reflection to keep the test focused and avoid standing up a full execution. `CreateTriggerOutputs` reads `instance.GlobalVariables[PollRunEnqueuer.PayloadVariableKey]`.

`Backend/Knotarium.Tests/Polling/PollingTriggerExecutorTests.cs`:

```csharp
using System.Collections.Generic;
using System.Reflection;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

public class PollingTriggerExecutorTests
{
    [Fact]
    public void CreateTriggerOutputs_PollingTrigger_EmitsPayloadOnResult()
    {
        var instance = new ExecutionInstance
        {
            Id = ExecutionInstanceId.New(),
            WorkflowDefinitionId = new WorkflowDefinitionId("wf-1"),
            TriggerOrigin = "poll",
            GlobalVariables = new Dictionary<string, object>
            {
                [PollRunEnqueuer.PayloadVariableKey] = "{\"v\":1}"
            }
        };

        var method = typeof(WorkflowExecutor).GetMethod(
            "CreateTriggerOutputs", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // CreateTriggerOutputs is an instance method but uses no instance state for this path;
        // construct via uninitialized object to avoid the heavy constructor.
        var executor = (WorkflowExecutor)System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(WorkflowExecutor));
        var outputs = (Dictionary<string, object>)method!.Invoke(executor, new object[] { "pollingTrigger", instance })!;

        Assert.True(outputs.ContainsKey("result"));
        Assert.Equal("{\"v\":1}", outputs["result"]);
    }

    [Fact]
    public void IsTriggerCompatibleWithOrigin_PollMapsToPollingTrigger()
    {
        var method = typeof(WorkflowExecutor).GetMethod(
            "IsTriggerCompatibleWithOrigin", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var compatible = (bool)method!.Invoke(null, new object[] { "pollingTrigger", "poll" })!;
        var notCompatible = (bool)method.Invoke(null, new object[] { "scheduler", "poll" })!;

        Assert.True(compatible);
        Assert.False(notCompatible);
    }
}
```

> NOTE: If `FormatterServices.GetUninitializedObject` is unavailable or flagged obsolete in this TFM, use `System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowExecutor))` instead. If `CreateTriggerOutputs` is changed to `static` (it has no instance dependencies for the polling/scheduler paths — consider making it static while here), invoke it with `BindingFlags.NonPublic | BindingFlags.Static` and a `null` target, dropping the uninitialized-object dance.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollingTriggerExecutorTests"`
Expected: FAIL — `result` key not present; `poll` origin not mapped.

- [ ] **Step 3: Update `CreateTriggerOutputs`**

In `WorkflowExecutor.cs`, replace the body of `CreateTriggerOutputs` (lines 1666-1675) with:

```csharp
    private Dictionary<string, object> CreateTriggerOutputs(string nodeType, ExecutionInstance instance)
    {
        var outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (nodeType.Equals("scheduler", StringComparison.OrdinalIgnoreCase))
        {
            outputs["triggeredAt"] = instance.CreatedAt;
        }
        else if (nodeType.Equals("pollingTrigger", StringComparison.OrdinalIgnoreCase))
        {
            if (instance.GlobalVariables is not null &&
                instance.GlobalVariables.TryGetValue("__pollPayload", out var payload) &&
                payload is not null)
            {
                outputs["result"] = payload;
            }
        }

        return outputs;
    }
```

> NOTE: The literal `"__pollPayload"` must equal `PollRunEnqueuer.PayloadVariableKey`. If `Knotarium.Features.Execution` can reference `Knotarium.Features.Polling` without a cycle (same assembly — yes), use the constant instead of the literal: `PollRunEnqueuer.PayloadVariableKey`.

- [ ] **Step 4: Update `IsTriggerCompatibleWithOrigin`**

In `WorkflowExecutor.cs`, add a `poll` branch to `IsTriggerCompatibleWithOrigin` (lines 1677-1691), before the final `return`:

```csharp
        if (triggerOrigin.Equals("poll", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("pollingTrigger", StringComparison.OrdinalIgnoreCase);
        }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollingTriggerExecutorTests"`
Expected: PASS (both).

- [ ] **Step 6: Commit**

```bash
git add Backend/Knotarium.Features/Execution/WorkflowExecutor.cs \
        Backend/Knotarium.Tests/Polling/PollingTriggerExecutorTests.cs
git commit -m "feat(polling): surface poll payload on result port and map poll origin"
```

---

## Task 10: `OpenApiPollSource` (Source B)

**Files:**
- Create: `Backend/Knotarium.Features/Polling/OpenApiPollSource.cs`
- Modify: `Backend/Knotarium.Api/Program.cs` (register the second `IPollSource`)
- Test: `Backend/Knotarium.Tests/Polling/OpenApiPollSourceTests.cs`

Reuses the existing OpenAPI interpreter to execute an imported operation, then applies the same change-detection. Body strategies (`hash`/`json-cursor`/`always`) go through `BodyChangeDetector`; `etag`/`last-modified` use response headers if the interpreter surfaces them, else fall back to hash.

- [ ] **Step 1: Inspect the interpreter's invocation surface**

Read `Backend/Knotarium.Features/OpenApi/OpenApiInterpreterExecutor.cs` to find the method that executes an operation given `serverConfigId`, `operationId`, `specVersion`, and inputs, and what it returns (status, body, headers). Note the exact method name and return type — the implementation below assumes a method `ExecuteAsync` returning an object exposing `Body` (string). **Adjust the call and the abstraction to match the real surface.**

- [ ] **Step 2: Write the failing test (against a small seam)**

To keep this testable without standing up specs, `OpenApiPollSource` depends on an injected delegate/interface `IOpenApiOperationInvoker` that returns the raw response. Define it and test the source over a stub.

`Backend/Knotarium.Core/Contracts/IOpenApiOperationInvoker.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>Minimal seam over the OpenAPI interpreter for polling: run an operation, get its response.</summary>
public interface IOpenApiOperationInvoker
{
    Task<OpenApiPollResponse> InvokeAsync(
        string serverConfigId, string operationId, string? specVersion, CancellationToken cancellationToken);
}

/// <summary>Raw response from an OpenAPI operation poll.</summary>
public sealed record OpenApiPollResponse(string Body, string? ETag, string? LastModified);
```

`Backend/Knotarium.Tests/Polling/OpenApiPollSourceTests.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

public class OpenApiPollSourceTests
{
    private sealed class StubInvoker : IOpenApiOperationInvoker
    {
        private readonly OpenApiPollResponse _response;
        public StubInvoker(OpenApiPollResponse response) => _response = response;
        public Task<OpenApiPollResponse> InvokeAsync(string s, string o, string? v, CancellationToken ct) =>
            Task.FromResult(_response);
    }

    [Fact]
    public async Task Hash_DetectsChangeOverOperationBody()
    {
        var source = new OpenApiPollSource(new StubInvoker(new OpenApiPollResponse("{\"v\":1}", null, null)));
        var config = "{\"changeDetection\":\"hash\",\"serverConfigId\":\"srv-1\",\"operationId\":\"listItems\"}";

        var first = await source.PollAsync(new PollContext(config, null), CancellationToken.None);
        var second = await source.PollAsync(new PollContext(config, first.NewCursor), CancellationToken.None);

        Assert.True(first.HasNew);
        Assert.False(second.HasNew);
    }

    [Fact]
    public async Task Etag_UsesResponseEtag()
    {
        var source = new OpenApiPollSource(new StubInvoker(new OpenApiPollResponse("{\"v\":1}", "\"e1\"", null)));
        var config = "{\"changeDetection\":\"etag\",\"serverConfigId\":\"srv-1\",\"operationId\":\"listItems\"}";

        var result = await source.PollAsync(new PollContext(config, null), CancellationToken.None);

        Assert.True(result.HasNew);
        Assert.Equal("\"e1\"", result.NewCursor);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~OpenApiPollSourceTests"`
Expected: FAIL — `OpenApiPollSource` does not exist.

- [ ] **Step 4: Implement `OpenApiPollSource`**

`Backend/Knotarium.Features/Polling/OpenApiPollSource.cs`:

```csharp
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>Polls an imported OpenAPI operation, reusing the interpreter via IOpenApiOperationInvoker.</summary>
public sealed class OpenApiPollSource : IPollSource
{
    private readonly IOpenApiOperationInvoker _invoker;

    public OpenApiPollSource(IOpenApiOperationInvoker invoker) => _invoker = invoker;

    public string Kind => "openapi";

    public async Task<PollResult> PollAsync(PollContext context, CancellationToken cancellationToken)
    {
        using var configDoc = JsonDocument.Parse(context.ConfigJson);
        var root = configDoc.RootElement;

        var serverConfigId = GetString(root, "serverConfigId")
            ?? throw new InvalidOperationException("OpenAPI poll source is missing 'serverConfigId'.");
        var operationId = GetString(root, "operationId")
            ?? throw new InvalidOperationException("OpenAPI poll source is missing 'operationId'.");
        var specVersion = GetString(root, "specVersion");
        var strategy = ParseStrategy(GetString(root, "changeDetection"));
        var jsonPath = GetString(root, "jsonCursorPath");

        var response = await _invoker.InvokeAsync(serverConfigId, operationId, specVersion, cancellationToken);

        return strategy switch
        {
            PollChangeDetection.Etag => FromValidator(response.ETag, context.Cursor, response.Body),
            PollChangeDetection.LastModified => FromValidator(response.LastModified, context.Cursor, response.Body),
            _ => BodyChangeDetector.Detect(strategy, response.Body, context.Cursor, jsonPath)
        };
    }

    private static PollResult FromValidator(string? validator, string? cursor, string body)
    {
        if (string.IsNullOrEmpty(validator))
        {
            return new PollResult(HasNew: true, Payload: body, NewCursor: cursor);
        }

        var hasNew = !string.Equals(validator, cursor, StringComparison.Ordinal);
        return new PollResult(hasNew, Payload: hasNew ? body : null, NewCursor: validator);
    }

    private static PollChangeDetection ParseStrategy(string? raw) => raw switch
    {
        "etag" => PollChangeDetection.Etag,
        "last-modified" => PollChangeDetection.LastModified,
        "hash" => PollChangeDetection.Hash,
        "json-cursor" => PollChangeDetection.JsonCursor,
        "always" => PollChangeDetection.Always,
        _ => PollChangeDetection.Hash
    };

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
```

> NOTE: `FromValidator` and `ParseStrategy`/`GetString` duplicate `HttpPollSource`. If you prefer, extract them into an internal static helper (`PollValidator`) shared by both sources before this commit — DRY. Keep it small and within the `Polling` namespace.

- [ ] **Step 5: Implement the real `IOpenApiOperationInvoker` adapter**

`Backend/Knotarium.Features/Polling/OpenApiOperationInvoker.cs` — a thin adapter over `OpenApiInterpreterExecutor` matching the surface you found in Step 1:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>Adapts the OpenAPI interpreter to the polling invoker seam.</summary>
public sealed class OpenApiOperationInvoker : IOpenApiOperationInvoker
{
    // TODO-DURING-IMPL: inject the actual interpreter/executor discovered in Step 1.
    private readonly OpenApi.OpenApiInterpreterExecutor _executor;

    public OpenApiOperationInvoker(OpenApi.OpenApiInterpreterExecutor executor) => _executor = executor;

    public async Task<OpenApiPollResponse> InvokeAsync(
        string serverConfigId, string operationId, string? specVersion, CancellationToken cancellationToken)
    {
        // Map to the executor's real ExecuteAsync signature + return type (confirmed in Step 1).
        var response = await _executor.ExecuteAsync(serverConfigId, operationId, specVersion, cancellationToken);
        return new OpenApiPollResponse(response.Body, response.ETag, response.LastModified);
    }
}
```

> NOTE: This adapter is the ONE place that touches the interpreter's concrete API. Its exact body MUST be rewritten to match what Step 1 found (method name, parameters, how to read body/headers). The `OpenApiPollSource` and all tests above are insulated from those details by `IOpenApiOperationInvoker`, so only this file changes. If the interpreter needs per-request inputs/auth resolved from `ServerConfig`, replicate what `OpenApiInterpreterExecutor`'s existing callers do.

- [ ] **Step 6: Register the OpenAPI source in `Program.cs`**

After the `HttpPollSource` registration added in Task 8 Step 2, add:

```csharp
builder.Services.AddScoped<Knotarium.Core.Contracts.IOpenApiOperationInvoker, Knotarium.Features.Polling.OpenApiOperationInvoker>();
builder.Services.AddScoped<Knotarium.Core.Contracts.IPollSource, Knotarium.Features.Polling.OpenApiPollSource>();
```

> NOTE: If sources are registered as singletons but the invoker (via the interpreter) needs scoped DB access, register `OpenApiPollSource`, `HttpPollSource`, and `PollSourceRegistry` all as **scoped** (per the Task 8 captive-dependency note) so they compose cleanly. The per-tick scope in `PollingWorker` makes scoped correct.

- [ ] **Step 7: Run tests**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~OpenApiPollSourceTests"` then the full suite `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj`.
Expected: PASS; no regressions; build succeeds.

- [ ] **Step 8: Commit**

```bash
git add Backend/Knotarium.Core/Contracts/IOpenApiOperationInvoker.cs \
        Backend/Knotarium.Features/Polling/OpenApiPollSource.cs \
        Backend/Knotarium.Features/Polling/OpenApiOperationInvoker.cs \
        Backend/Knotarium.Api/Program.cs \
        Backend/Knotarium.Tests/Polling/OpenApiPollSourceTests.cs
git commit -m "feat(polling): add OpenApiPollSource reusing the OpenAPI interpreter"
```

---

## Task 11: Frontend config form

**Files:**
- Create: `Frontend/src/components/PollingTriggerPropertyForm.tsx`
- Modify: `Frontend/src/components/PropertiesPanel.tsx` (route `pollingTrigger` to the new form)

The form lets the user pick interval, source kind, change-detection strategy, and the matching source fields. Reuse existing controls (credential dropdown, resource picker) the way `RestCallerPropertyForm` and `ResourcePickerPropertyForm` do.

- [ ] **Step 1: Inspect the existing property forms**

Read `Frontend/src/components/PropertiesPanel.tsx` (find where it switches to `RestCallerPropertyForm` / `ResourcePickerPropertyForm`) and `RestCallerPropertyForm.tsx` to copy the props contract (how a form receives the node, reads/writes `properties`, and the credential dropdown + resource picker components). Match that contract exactly.

- [ ] **Step 2: Create `PollingTriggerPropertyForm.tsx`**

Implement a form whose props mirror `RestCallerPropertyForm`'s (e.g. `{ node, onChange }` or whatever the codebase uses). It must:
- Render a number input bound to `properties.intervalSeconds`.
- Render a `sourceKind` select (`http` | `openapi`).
- Render a `changeDetection` select (`etag` | `last-modified` | `hash` | `json-cursor` | `always`), and a `jsonCursorPath` text input shown only when `changeDetection === 'json-cursor'`.
- When `sourceKind === 'http'`: text inputs for `url`, `method`, `headersJson`, and the existing credential dropdown bound to `apiKeySecretRef`.
- When `sourceKind === 'openapi'`: the existing resource picker bound to `serverConfigId` / `operationId` / `specVersion`.

Write it following the exact patterns and imports observed in Step 1 (this codebase's component style, not invented APIs). Keep all writes going through the same `onChange`/properties-update mechanism the sibling forms use.

> NOTE: No invented component names. Every control used here must be one that already exists in `RestCallerPropertyForm` / `ResourcePickerPropertyForm` / `ManifestForm`. If a conditional-field helper exists, use it; otherwise plain `&&` conditional JSX is fine.

- [ ] **Step 3: Route the node type in `PropertiesPanel.tsx`**

In the same switch/branch that selects `RestCallerPropertyForm` for HTTP nodes, add a branch: when the selected node's type is `pollingTrigger`, render `PollingTriggerPropertyForm` with the same props the siblings receive. Import it at the top alongside the other form imports.

- [ ] **Step 4: Verify the frontend builds and renders**

Run the frontend build/typecheck (match the project's script — check `Frontend/package.json`):
Run: `cd Frontend && npm run build` (or `npm run typecheck` / `npm run lint` if those exist)
Expected: builds with no type errors.

Then manually verify in the running app (per the `run` skill): add a Polling Trigger node, confirm the form shows interval + source/strategy selectors, and that switching `sourceKind` swaps the HTTP/OpenAPI fields.

- [ ] **Step 5: Commit**

```bash
git add Frontend/src/components/PollingTriggerPropertyForm.tsx Frontend/src/components/PropertiesPanel.tsx
git commit -m "feat(polling): add Polling Trigger property form"
```

---

## Task 12: End-to-end integration test

**Files:**
- Test: `Backend/Knotarium.Tests/Polling/PollingEndToEndTests.cs`

Proves the spine: a changed response enqueues exactly one run with the payload reachable; an unchanged response enqueues none. Uses the real `PollEvaluationService` + `PollSourceRegistry` + `HttpPollSource` with a stub HTTP handler, and the real `PollRunEnqueuer` against a SQLite context with a seeded active version.

- [ ] **Step 1: Write the integration test**

`Backend/Knotarium.Tests/Polling/PollingEndToEndTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Knotarium.Features.Polling;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knotarium.Tests.Polling;

public class PollingEndToEndTests
{
    // Reuse the StubHandler/StubFactory/NullSecretResolver shapes from HttpPollSourceTests
    // (either make those internal+shared or duplicate the tiny doubles here).

    [Fact]
    public async Task ChangedResponse_EnqueuesExactlyOneRun_WithPayload()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(5000));

            // Seed an enabled workflow + active version so the enqueuer creates a run.
            // Use the same seeding the existing execution tests use (search Tests for ActiveWorkflowVersion seeding).
            await SeedEnabledWorkflowWithActiveVersionAsync(options, "wf-1");
            await SeedTriggerAsync(options, "wf-1", DateTimeOffset.UnixEpoch, changeDetection: "hash");

            var responses = new Queue<string>(new[] { "{\"v\":1}", "{\"v\":1}", "{\"v\":2}" });
            var handler = new HttpPollSourceTests_StubHandler(() => responses.Dequeue());
            var registry = new PollSourceRegistry(new IPollSource[]
            {
                new HttpPollSource(new HttpPollSourceTests_StubFactory(handler), new HttpPollSourceTests_NullSecret())
            });

            async Task RunOnceAsync()
            {
                using var db = new AppDbContext(options);
                var enqueuer = new PollRunEnqueuer(db, /* queue */ TestQueue(), TestActiveVersionService(db), time);
                var service = new PollEvaluationService(db, registry, enqueuer, time, NullLogger<PollEvaluationService>.Instance);
                await service.EvaluateDuePollsAsync(CancellationToken.None);
                time.Advance(TimeSpan.FromSeconds(60)); // make the trigger due again
            }

            await RunOnceAsync(); // v=1 (first poll: new)
            await RunOnceAsync(); // v=1 (unchanged: no run)
            await RunOnceAsync(); // v=2 (changed: new)

            using var verify = new AppDbContext(options);
            var runs = await verify.ExecutionInstances.Where(e => e.TriggerOrigin == "poll").ToListAsync();
            Assert.Equal(2, runs.Count);
            Assert.All(runs, r => Assert.True(r.GlobalVariables.ContainsKey(PollRunEnqueuer.PayloadVariableKey)));
        }
        finally { connection.Dispose(); }
    }
}
```

> NOTE: This test references several helpers that must be created or adapted during implementation, because they depend on concrete types confirmed in earlier tasks:
> - `HttpPollSourceTests_StubHandler` / `StubFactory` / `NullSecret`: promote the doubles from `HttpPollSourceTests` to shared `internal` classes in `PollingTestSupport.cs` (DRY) and reference them here; the stub handler returns `200 OK` with the supplied body.
> - `TestQueue()`: construct a real `WorkflowExecutionQueue` (it is an in-memory `Channel` per the Explore — no external dependency).
> - `TestActiveVersionService(db)`: construct `ActiveWorkflowVersionService` the same way the existing execution tests do (search Tests for its construction).
> - `SeedEnabledWorkflowWithActiveVersionAsync` / `SeedTriggerAsync`: seed rows directly; copy the active-version seeding from an existing execution/enqueue test.
> Adjust the doubles/constructors until it compiles — the assertions (2 runs, payload present) are the contract that must not change.

- [ ] **Step 2: Run the integration test**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj --filter "FullyQualifiedName~PollingEndToEndTests"`
Expected: PASS.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add Backend/Knotarium.Tests/Polling/PollingEndToEndTests.cs Backend/Knotarium.Tests/Polling/PollingTestSupport.cs
git commit -m "test(polling): end-to-end changed/unchanged enqueue behavior"
```

---

## Task 13: Update memory + final verification

**Files:**
- Create: `C:\Users\akn\.claude\projects\D--Private-Source-AknSideProjects-Automate\memory\polling-trigger.md`
- Modify: `C:\Users\akn\.claude\projects\D--Private-Source-AknSideProjects-Automate\memory\MEMORY.md`

- [ ] **Step 1: Full build + test**

Run: `dotnet build Backend/Knotarium.sln && dotnet test Backend/Knotarium.Tests/Knotarium.Tests.csproj`
Expected: build succeeds; all tests pass.

- [ ] **Step 2: Manual smoke (per `run` skill)**

Arm the runtime, create a workflow with a Polling Trigger (HTTP, `always`, short interval) feeding a Log node, save, and confirm runs appear on the interval; switch to `hash` against a static endpoint and confirm runs stop after the first.

- [ ] **Step 3: Record a memory**

Write `polling-trigger.md` (type `project`) summarizing: the `pollingTrigger` node + `PollingTrigger` table mirror the scheduler spine; `IPollSource` seam (`http`/`openapi`); cursor semantics; payload via `GlobalVariables["__pollPayload"]` → `result` port; `TriggerOrigin="poll"`. Add a one-line pointer to `MEMORY.md`. Link `[[workflow-activation-semantics]]`.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "docs(polling): record polling-trigger design in memory"
```

---

## Self-Review Notes

- **Spec coverage:** node manifest (T1), persistence + cursor (T2), `IPollSource` spine (T3, T5), HTTP source incl. all 4 strategies (T3 detector + T4), OpenAPI source (T10), synchronizer with cursor-preserve/reset rule (T6), worker + arming/IsEnabled gating (T7, T8), payload→run wiring (T7 enqueuer + T9 executor), error handling/`LastError` (T7), frontend (T11), tests incl. integration (every task + T12). All spec sections map to a task.
- **Payload key consistency:** `PollRunEnqueuer.PayloadVariableKey == "__pollPayload"` is the single source of truth; T9 reads the same literal/constant.
- **Type consistency:** `PollResult(HasNew, Payload, NewCursor)`, `PollContext(ConfigJson, Cursor)`, `PollChangeDetection` enum, `IPollSource.Kind`/`PollAsync`, `PollEvaluationService.EvaluateDuePollsAsync`, `IPollRunEnqueuer.EnqueueAsync` are used identically across tasks.
- **Known impl-time confirmations (flagged inline, not placeholders):** manifest enum field name (T1), migration vs manual-DDL approach (T2), `NodeDefinition` construction (T6), `ActiveWorkflowVersionService`/`WorkflowExecutionQueue` signatures (T7), uninitialized-object reflection helper (T9), the OpenAPI interpreter's real `ExecuteAsync` surface (T10), and the frontend form props contract (T11). Each is isolated behind a seam so only one file absorbs the concrete detail.
