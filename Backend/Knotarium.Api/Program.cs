// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Channels;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Api.Services;
using Knotarium.Api.Services.Ai;
using Knotarium.Features.Bundles;
using Knotarium.Features.Compiler;
using Knotarium.Features.Execution;
using Knotarium.Features.Portability;
using Knotarium.Features.NodeEditor;
using Knotarium.Features.Nodes;
using Knotarium.Features.Notifications;
using Knotarium.Features.Schedules;
using Knotarium.Features.Settings;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Persistence.OpenApi;
using Knotarium.Infrastructure.OpenApi;
using Knotarium.Infrastructure.Security;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Features.OpenApi;
using Knotarium.Api;
using Knotarium.Api.Services.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Allow the same executable to run as a Windows service. This is a no-op unless the process was
// actually launched by the Service Control Manager, so interactive/console and dev runs are
// unaffected. Under SCM it wires up the service lifetime handshake (so the service reaches the
// "Running" state instead of timing out) and pins the content root to the exe directory rather than
// System32. The installer's service-install mode relies on this.
builder.Host.UseWindowsService();

builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    loggerConfiguration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Destructure.ByTransforming<Dictionary<string, object>>(LogSanitizer.MaskDictionary)
        .WriteTo.Console(new RenderedCompactJsonFormatter());
});

// Directory of the actual executable. NOTE: for a single-file self-extract build,
// AppContext.BaseDirectory is the temp extraction folder, so we use Environment.ProcessPath
// to find where the exe (and the wwwroot / db beside it) really live.
var appBaseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

// Machine-wide data home shared across launch modes. A Windows service and an interactive run started
// from different exe paths would otherwise each anchor their own SQLite DB + at-rest credential key
// (next to their own exe) and never see each other's data. Anchoring both under one fixed directory
// (%ProgramData%\Knotarium on Windows, the platform CommonApplicationData elsewhere) keeps them in
// sync. Overridable via Storage:DataDirectory (or Storage__DataDirectory env) for containers/Linux
// where CommonApplicationData isn't the right home. Development keeps the plain relative paths.
var dataDir = builder.Configuration["Storage:DataDirectory"];
if (string.IsNullOrWhiteSpace(dataDir))
{
    dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Knotarium");
}
if (!builder.Environment.IsDevelopment())
{
    Directory.CreateDirectory(dataDir);
}

// Database provider factory + AppDbContext + provider-selected, telemetry-instrumented journal writer.
builder.Services.AddPersistence(builder.Configuration, dataDir, builder.Environment.IsDevelopment());

// Register Core/Features/Infrastructure services
// Binary node-package support: the registry holds executors loaded from prebuilt *.dll packages
// on disk; the watcher scans the package folder at startup (and hot-reloads in dev). Registered
// before the manifest provider, which now consults the registry to surface binary packages.
builder.Services.AddSingleton<Knotarium.NodeRuntime.INodeExecutorRegistry, Knotarium.NodeRuntime.NodeExecutorRegistry>();
builder.Services.AddHostedService<Knotarium.NodeRuntime.NodePackageWatcher>();
builder.Services.AddCompiler();   // WorkflowCompiler + built-in InMemoryNodePackageManifestProvider
builder.Services.AddSingleton<DbNodePackageManifestProvider>();
builder.Services.AddSingleton<INodePackageManifestProvider>(sp => sp.GetRequiredService<DbNodePackageManifestProvider>());
builder.Services.AddSingleton<INodePackageCatalogProvider>(sp => sp.GetRequiredService<DbNodePackageManifestProvider>());

// Binary host-service plugins (in-process signal providers, option-loaders, background loops).
builder.Services.AddHostPlugins(builder.Configuration);

builder.Services.AddScoped<DatabaseWorkflowStore>();
builder.Services.AddScoped<FileWorkflowStore>();
builder.Services.AddScoped<IWorkflowStore>(sp => sp.GetRequiredService<FileWorkflowStore>());
builder.Services.AddScoped<IWorkflowDefinitionProvider>(sp => sp.GetRequiredService<DatabaseWorkflowStore>());
builder.Services.AddSingleton<SseEventPublisher>();
builder.Services.AddSingleton<IExecutionEventPublisher>(sp => sp.GetRequiredService<SseEventPublisher>());
// In a productive (non-Development) build, auto-generate + persist the at-rest credential key in the
// shared data directory when none is configured, so a copy-and-run bundle works without manual key setup
// (the key stays out of the DB) and every launch mode resolves the same key. In Development a missing key
// stays a configuration error (no directory → no provisioning).
builder.Services.AddSingleton(new Knotarium.Infrastructure.Security.CredentialKeyProvisioning(
    builder.Environment.IsDevelopment() ? null : dataDir));
builder.Services.AddSingleton<ICredentialCipher, AesCredentialCipher>();
builder.Services.AddScoped<CredentialAccessor>();
builder.Services.AddScoped<ISecretResolver>(sp => sp.GetRequiredService<CredentialAccessor>());
builder.Services.AddScoped<ICredentialAccessor>(sp => sp.GetRequiredService<CredentialAccessor>());
builder.Services.AddSingleton<HttpEgressPolicyEvaluator>();
builder.Services.AddTransient<HttpEgressPolicyHandler>();

// Every named HTTP client that can reach a user- or config-supplied URL MUST be registered through
// this helper. The egress message handler validates the literal URI, and the primary handler
// validates every physical connection (DNS-aware) and pins the socket to a vetted address, so
// redirect hops and DNS-rebinding can't reach private/metadata IPs — see HttpEgressPolicyEvaluator.
// A plain factory.CreateClient()/CreateClient("unregistered") is NOT guarded, so it must never be
// used for outbound requests to a destination the caller doesn't fully control.
void AddEgressGuardedHttpClient(string name, bool allowInsecureCertificate = false)
{
    builder.Services.AddHttpClient(name)
        .AddHttpMessageHandler<HttpEgressPolicyHandler>()
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var evaluator = sp.GetRequiredService<HttpEgressPolicyEvaluator>();
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                ConnectCallback = (context, ct) => evaluator.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct),
            };
            if (allowInsecureCertificate)
            {
                // Skip TLS *chain* validation only; the egress/private-network rules still apply.
                handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                };
            }

            return handler;
        });
}

// General-purpose egress-guarded client (http/httpRequest node, OpenAPI caller, polling, AI
// providers, dynamic-options loaders, OAuth token fetch, inline/compiled scripts' context.Http).
AddEgressGuardedHttpClient("HttpNode");
// Dedicated client for reaching a server that presents a self-signed / untrusted certificate.
// Used only when explicitly opted in (per-import flag, or a ServerConfig with AllowInsecureCertificate).
AddEgressGuardedHttpClient("InsecureHttp", allowInsecureCertificate: true);
// Outbound failure-alert / notification channels post to admin-configured URLs — still SSRF sinks,
// so they run through the egress guard like everything else.
AddEgressGuardedHttpClient("NotificationWebhook");
AddEgressGuardedHttpClient("NotificationSlack");
builder.Services.AddNodeEditor();
builder.Services.AddSingleton<ICorrelationTokenCrypto, CorrelationTokenCrypto>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICorrelationTokenService, CorrelationTokenService>();
builder.Services.AddSchedules();   // IWorkflowEnqueueService + IScheduleEvaluationService
builder.Services.AddScoped<WorkflowScheduleSynchronizer>();
builder.Services.AddScoped<WorkflowPollingTriggerSynchronizer>();
// Expose the two host synchronizers through the Core seam so the (now Features-resident) publish/
// activation services can depend on IEnumerable<IWorkflowTriggerSynchronizer> instead of the concrete
// host bridges. Same scoped instances the create/update endpoints resolve directly.
builder.Services.AddScoped<Knotarium.Core.Contracts.IWorkflowTriggerSynchronizer>(
    sp => sp.GetRequiredService<WorkflowScheduleSynchronizer>());
builder.Services.AddScoped<Knotarium.Core.Contracts.IWorkflowTriggerSynchronizer>(
    sp => sp.GetRequiredService<WorkflowPollingTriggerSynchronizer>());
// External-signal (Event/Action Trigger) activation registry: in-process analogue of the schedule/
// polling synchronizers. Singleton (holds live provider subscriptions); a startup reconciler
// rehydrates triggers for already-enabled workflows. No-ops when no provider plugin is loaded.
// (IExternalSignalRunEnqueuer is registered by AddExecution() below.)
builder.Services.AddSingleton<Knotarium.Api.Services.ExternalSignalTriggerRegistry>(sp =>
    new Knotarium.Api.Services.ExternalSignalTriggerRegistry(
        sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Knotarium.Api.Services.ExternalSignalTriggerRegistry>>(),
        sp.GetRequiredService<Knotarium.Api.Services.RuntimeArmingState>(),
        sp.GetService<Knotarium.Core.Contracts.IExternalSignalProvider>()));
builder.Services.AddHostedService<Knotarium.Api.Services.ExternalSignalStartupReconciler>();
builder.Services.AddPolling();   // evaluation + run-enqueuer + poll-source registry & built-in sources
builder.Services.AddScoped<WorkflowPublisher>();
builder.Services.AddScoped<ActiveWorkflowVersionService>();
builder.Services.AddScoped<WorkflowActivationService>();
builder.Services.AddScoped<Knotarium.Api.Services.WorkflowLifecycleService>();
// Workflow-portability family (folder export, .kgbundle bundles, .kgtpl templates, .kgbak backup).
builder.Services.AddPortability();

builder.Services.AddScoped<IOpenApiParser, MicrosoftOpenApiParser>();
builder.Services.AddScoped<IOpenApiSpecStore, OpenApiSpecStore>();
builder.Services.AddScoped<IServerConfigStore, ServerConfigStore>();
builder.Services.AddSingleton<IOAuthTokenCache, InMemoryOAuthTokenCache>();
// EF-backed Core adapters seaming each feature slice off persistence (incl. the Settings slice's store).
builder.Services.AddPersistenceAdapters();
builder.Services.AddOpenApiFeature();   // generator + import/delete handlers + interpreter/auth inversion seams

// AI workflow generation. The Features side — bound options, provider-config store, vendor adapters
// and the scoped generator/orchestrator — registers via AddAiGeneration(); the generator/orchestrator
// are scoped because they pull the scoped WorkflowCompiler + ISecretResolver. The host-side runner,
// job store and queue (singletons shared with the hosted worker that runs each job in its own scope)
// stay here in the composition root.
builder.Services.AddAiGeneration(builder.Configuration);
builder.Services.AddScoped<Knotarium.Features.Ai.GeneratedCredentialFinalizer>();
builder.Services.AddScoped<Knotarium.Features.Ai.IAiGenerationRunner, Knotarium.Features.Ai.AiGenerationRunner>();
builder.Services.AddSingleton<Knotarium.Features.Ai.AiGenerationJobStore>();
builder.Services.AddSingleton<Knotarium.Features.Ai.AiGenerationQueue>();
builder.Services.AddHostedService<Knotarium.Features.Ai.AiGenerationWorker>();

// Notification / failure-alert spine + error-workflow spine. Each pairs a singleton queue (written
// from the scoped executor, so it must be a singleton to be the same instance the worker reads) with a
// hosted worker plus the channel senders. The error-workflow run-enqueuer is part of AddExecution().
builder.Services.AddNotifications();
builder.Services.AddSettings();   // GlobalSettingsService (its ISettingsStore adapter is in AddPersistenceAdapters)


// Execution slice: queue + hosted worker + executor/recovery/replay + the external-signal and
// error-workflow run-enqueuers, registered together (lifetime coupling encoded in AddExecution()).
builder.Services.AddExecution();
// Global runtime arming switch — seeded from config so the server can boot armed
// headlessly ("Runtime:Armed": true); defaults to disarmed (safe / design-time). Host-owned, as are
// the scheduling/polling hosted workers below.
builder.Services.AddSingleton(new RuntimeArmingState(builder.Configuration.GetValue("Runtime:Armed", false)));
builder.Services.AddSingleton<Knotarium.Core.Contracts.IRuntimeArmingState>(sp => sp.GetRequiredService<RuntimeArmingState>());
builder.Services.AddHostedService<SchedulingWorker>();
builder.Services.AddHostedService<PollingWorker>();
// Bounds run-history growth: prunes old terminal runs (cascades to their journal + node states).
builder.Services.AddHostedService<JournalRetentionWorker>();
// Pauses arming (stops new runs) when free disk on the data volume falls below a threshold.
builder.Services.AddHostedService(sp => new DiskSpaceGuardWorker(
    sp.GetRequiredService<RuntimeArmingState>(),
    builder.Configuration,
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DiskSpaceGuardWorker>>(),
    dataDir));

// Built-in node tasks + node-task registry + shared Roslyn script compiler.
builder.Services.AddBuiltInNodes();

// Dynamic-options / resource-locator loaders (loader registry, REST loader, cache, resolver).
// Plugin-provided loaders are registered separately above from the host plugin registry.
builder.Services.AddOptionsFeature();

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(ExecutionTelemetry.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
    })
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(ExecutionTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
    });

// CORS. When Cors:AllowedOrigins is configured, restrict to that allowlist (and allow credentials so a
// legitimately cross-origin SPA can carry the session cookie). With no allowlist the SPA is same-origin
// (prod wwwroot / dev Vite proxy) and needs no cross-origin access, so the default stays permissive but
// WITHOUT credentials — AllowAnyOrigin forbids credentials, so an authenticated session can't be reused
// cross-origin. Tighten by setting Cors:AllowedOrigins to your SPA origin(s).
var corsAllowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsAllowedOrigins.Length > 0)
        {
            policy.WithOrigins(corsAllowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

// Rate limiting for the abuse-prone surfaces: interactive login and the anonymous machine routes
// (webhook trigger + resume). See RateLimitPolicies.
builder.Services.AddKnotariumRateLimiting(builder.Configuration);

// Optional per-workflow webhook secret guarding the anonymous POST /api/executions trigger.
builder.Services.AddScoped<Knotarium.Api.Services.WebhookSecretService>();

// Authentication (Gap 3, step 1): cookie-based login gating the management API. On by default; the
// integration-test factory sets Auth:Enabled=false so unauthenticated endpoint tests keep working.
// The SPA is same-origin (prod wwwroot; dev via the Vite /api proxy), so the session cookie flows
// without cross-origin credential handling.
var authEnabled = builder.Configuration.GetValue("Auth:Enabled", true);

// In no-auth mode every endpoint (including the capability toggle + Inline Code) is anonymous, which is
// only safe on a loopback-bound instance. Refuse to start if configuration binds a non-loopback address
// without an explicit opt-in, so "Auth:Enabled=false" on 0.0.0.0 can't silently expose an unauthenticated
// RCE surface to the LAN. Enforced outside Development (dev binds plain HTTP / device IPs freely); a warning
// is logged in Development.
if (!authEnabled)
{
    var nonLoopback = Knotarium.Api.Services.LoopbackBindingGuard.NonLoopbackBindings(builder.Configuration);
    var overrideAllowed = builder.Configuration.GetValue(Knotarium.Api.Services.LoopbackBindingGuard.OverrideConfigKey, false);
    if (nonLoopback.Count > 0 && !overrideAllowed)
    {
        var message =
            "Authentication is disabled (Auth:Enabled=false) but the server is configured to bind a non-loopback " +
            $"address ({string.Join(", ", nonLoopback)}). In no-auth mode every endpoint is anonymous, so this would " +
            "expose an unauthenticated remote-code-execution surface. Bind loopback only (e.g. http://127.0.0.1:PORT), " +
            "enable authentication (Auth:Enabled=true), or set " +
            $"{Knotarium.Api.Services.LoopbackBindingGuard.OverrideConfigKey}=true to accept the risk (e.g. behind a trusted reverse proxy).";
        if (builder.Environment.IsDevelopment())
        {
            Console.Error.WriteLine("[WARN] " + message);
        }
        else
        {
            throw new InvalidOperationException(message);
        }
    }
}

// Exposed so endpoints can enforce admin-only mutations (a no-op when auth is disabled).
builder.Services.AddSingleton(new Knotarium.Api.Services.Auth.AuthOptions(authEnabled));
builder.Services.AddScoped<UserService>();
if (authEnabled)
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "kg_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Never send the session cookie over cleartext HTTP in a real deployment. In Development the
            // dev server / tests run plain HTTP, so relax to SameAsRequest there; everywhere else the
            // cookie is HTTPS-only (defends the LAN self-hosted plain-HTTP case).
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            // This is an API, not an MVC app: answer 401/403 instead of redirecting to a login page.
            options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
    // Secure-by-default: every endpoint requires an authenticated user unless it opts out with
    // AllowAnonymous (the auth bootstrap routes, the machine-facing webhook/resume triggers, and the
    // SPA fallback).
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    });
}

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();
app.UseHttpsRedirection();

// Serve the bundled single-page UI when a built `wwwroot` sits next to the executable
// (the productive build copies Frontend/dist there). The frontend calls the API via
// relative `/api` URLs, so serving it same-origin needs no extra config. In dev the UI
// runs on Vite and there's no wwwroot, so this block is skipped entirely.
var spaRoot = Path.Combine(appBaseDir, "wwwroot");
var serveSpa = File.Exists(Path.Combine(spaRoot, "index.html"));
IFileProvider? spaFileProvider = null;
if (serveSpa)
{
    spaFileProvider = new PhysicalFileProvider(spaRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaFileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = spaFileProvider });
}

// Auth runs after static files (so the SPA shell + assets load without a session) and before the API
// endpoints it gates.
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    // CSRF defense-in-depth (opt-in): reject an authenticated, state-changing request whose Origin header
    // is present but doesn't match this host or a configured CORS origin. Layered on top of the SameSite=Lax
    // session cookie. Off by default because a reverse proxy can rewrite Host and cause false positives;
    // enable with Security:EnforceSameOrigin=true on a direct-bound deployment.
    if (builder.Configuration.GetValue("Security:EnforceSameOrigin", false))
    {
        app.Use(async (ctx, next) =>
        {
            var method = ctx.Request.Method;
            var isUnsafe = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
                || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
            if (isUnsafe && ctx.User.Identity?.IsAuthenticated == true)
            {
                var origin = ctx.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin))
                {
                    var sameOrigin = string.Equals(origin, $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase);
                    var allowed = sameOrigin || corsAllowedOrigins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
                    if (!allowed)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await ctx.Response.WriteAsJsonAsync(new { message = "Cross-origin state-changing request rejected." });
                        return;
                    }
                }
            }
            await next(ctx);
        });
    }
}

// Ordered startup work: bring the SQLite schema up to date, verify the audit chain, heal legacy
// socket mappings on stored graphs. See StartupInitializer.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    Knotarium.Api.Services.StartupInitializer.MigrateSchema(db);
    await Knotarium.Api.Services.StartupInitializer.VerifyAuditChainAsync(db);
    await Knotarium.Api.Services.StartupInitializer.HealSocketMappingsAsync(db, startupLogger);

    // Restore the persisted arming state (the operator's last explicit choice via the arming endpoint).
    // Precedence: persisted value > "Runtime:Armed" config seed > disarmed. A fresh install therefore
    // never starts armed on its own, but an instance armed once stays armed across restarts.
    var globalSettings = scope.ServiceProvider.GetRequiredService<Knotarium.Features.Settings.GlobalSettingsService>();
    if (await globalSettings.GetAsync(Knotarium.Core.Domain.AppSettingKeys.RuntimeArmed) is { } persistedArmed)
    {
        var armed = string.Equals(persistedArmed, "true", StringComparison.OrdinalIgnoreCase);
        scope.ServiceProvider.GetRequiredService<RuntimeArmingState>().SetArmed(armed);
        startupLogger.LogInformation("Restored persisted runtime arming state: {Armed}.", armed);
    }

    // Optional headless bootstrap: seed the first admin from configuration when no users exist yet
    // (Auth:InitialAdmin:Username/Password). Without it, the first admin is created via the SPA's
    // first-run setup screen.
    if (authEnabled)
    {
        var userService = scope.ServiceProvider.GetRequiredService<UserService>();
        var seedUser = builder.Configuration["Auth:InitialAdmin:Username"];
        var seedPassword = builder.Configuration["Auth:InitialAdmin:Password"];
        if (!string.IsNullOrWhiteSpace(seedUser) && !string.IsNullOrWhiteSpace(seedPassword) && await userService.CountAsync() == 0)
        {
            try
            {
                await userService.CreateAsync(seedUser, seedPassword, "admin");
                startupLogger.LogInformation("Seeded initial admin '{User}' from configuration.", seedUser);
            }
            catch (Exception ex)
            {
                startupLogger.LogWarning(ex, "Failed to seed initial admin from configuration.");
            }
        }
    }
}

app.MapAuthEndpoints();
app.MapWorkflowGroupEndpoints();
app.MapWorkflowEndpoints();
app.MapWorkflowVersionEndpoints();

// Vendor-setting import (host hook §8): a generic surface over plugin-contributed
// IWorkflowImportProvider capabilities — list providers, preview the coverage report for an uploaded
// file, and install the generated workflows as inactive versions. The host never sees vendor types —
// only a generic WorkflowDefinition + a report cross the seam.
app.MapImportProviderEndpoints();

app.MapBundleEndpoints();
app.MapTemplateEndpoints();
app.MapAdminBackupEndpoints();
app.MapWorkflowTriggerEndpoints();
app.MapRuntimeSettingsEndpoints();
app.MapExecutionEndpoints();
app.MapWebhookSecretEndpoints();
app.MapCredentialEndpoints();
app.MapNotificationChannelEndpoints();
app.MapHostEndpoints();
app.MapInlineCodeTestEndpoint();
app.MapAiGenerationEndpoints();
app.MapOptionsEndpoint();
app.MapExternalSystemsEndpoint();
app.MapNodePackageEndpoints();
app.MapExecutionEventStreamEndpoints();
app.MapOpenApiSpecEndpoints();
app.MapServerConfigEndpoints();

// SPA fallback: any non-API GET that didn't match a static file returns index.html so
// client-side routing works on deep links / refresh. Mapped last, lowest priority, so it
// never shadows the /api endpoints above.
if (serveSpa && spaFileProvider is not null)
{
    // Anonymous: the SPA shell must load so its login/setup screen can render before any session exists.
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spaFileProvider }).AllowAnonymous();
}

app.Run();



public partial class Program { }

