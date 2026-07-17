// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Core.Domain;
using Knotarium.Core.Domain.OpenApi;
using Knotarium.Features.OpenApi;
using Knotarium.NodeRuntime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Knotarium.Tests.OpenApi;

/// <summary>
/// Exercises <see cref="OpenApiInterpreterExecutor"/> end-to-end with in-memory fakes.
/// Tests cover URL building, arg placement, and all auth modes. Under Option C this is the
/// single pre-compiled executor every openapi.* node runs through — no Roslyn compilation.
/// </summary>
public class RestCallerExecutorTests
{
    // -------------------------------------------------------------------------
    // Spec / Config builders
    // -------------------------------------------------------------------------

    private static ParsedSpec BuildSpec(
        string id,
        string operationId = "getItem",
        string method = "GET",
        string pathTemplate = "/items/{id}",
        IReadOnlyList<ApiParameter>? parameters = null,
        ApiRequestBody? requestBody = null,
        IReadOnlyList<SecurityScheme>? securitySchemes = null)
    {
        var op = new ApiOperation(
            operationId, method, pathTemplate, null,
            Array.Empty<string>(),
            parameters ?? Array.Empty<ApiParameter>(),
            requestBody,
            Array.Empty<string>());

        return new ParsedSpec(
            new ImportedSpec(new OpenApiSpecId(id), "Test API", "1.0", "openapi3.0",
                Array.Empty<string>(), Array.Empty<string>(), DateTimeOffset.UtcNow, 1),
            new[] { op },
            Array.Empty<ApiSchema>(),
            securitySchemes ?? Array.Empty<SecurityScheme>());
    }

    private static ServerConfigInfo MakeServerConfig(
        string id = "srv1",
        string baseUrl = "https://api.example.com",
        string securityType = "none",
        string? credentialRef = null,
        Dictionary<string, string>? serverVariables = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ServerConfigInfo(id, "Test", baseUrl,
            serverVariables ?? new Dictionary<string, string>(),
            securityType, credentialRef, now, now);
    }

    // -------------------------------------------------------------------------
    // Executor helpers
    // -------------------------------------------------------------------------

    // Option C: there is nothing to compile — every openapi.* node runs through the single
    // pre-compiled OpenApiInterpreterExecutor. The dispatcher supplies the spec id via the
    // reserved __specId input (see MakeInput), so we construct the executor directly here.
    private static INodeExecutor CompileAndInstantiate(
        ParsedSpec spec,
        IOpenApiSpecStore specStore,
        IServerConfigStore serverConfigStore,
        IOAuthTokenCache? oAuthTokenCache = null)
        => new OpenApiInterpreterExecutor(specStore, serverConfigStore, oAuthTokenCache);

    private static IEnumerable<MetadataReference> BuildReferences()
    {
        var assemblies = new[]
        {
            typeof(object).Assembly,
            typeof(INodeExecutor).Assembly,        // Knotarium.Core
            typeof(IOAuthTokenCache).Assembly,
            typeof(CollectibleAssemblyLoadContext).Assembly,
            typeof(HttpRequestMessage).Assembly,
            typeof(ILogger).Assembly,
            Assembly.Load("System.Runtime"),
            Assembly.Load("System.Collections"),
            Assembly.Load("System.Threading.Tasks"),
            Assembly.Load("System.Text.Json"),
            Assembly.Load("System.Private.Uri"),
            Assembly.Load("System.Net.Primitives"),
            Assembly.Load("System.Memory"),
            Assembly.Load("netstandard"),
        };

        return assemblies
            .Select(a => a.Location)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    // -------------------------------------------------------------------------
    // Fake services
    // -------------------------------------------------------------------------

    private sealed class FakeSpecStore : IOpenApiSpecStore
    {
        private readonly ParsedSpec _spec;
        public bool GetLatestCalled { get; private set; }
        public int? GetVersionCalledWith { get; private set; }

        public FakeSpecStore(ParsedSpec spec) => _spec = spec;

        public Task<ImportedSpec> SaveAsync(ParsedSpec spec, CancellationToken ct) =>
            Task.FromResult(spec.Metadata);

        public Task<(ImportedSpec Spec, ParsedSpec Full)?> GetLatestAsync(OpenApiSpecId id, CancellationToken ct)
        {
            GetLatestCalled = true;
            (ImportedSpec, ParsedSpec)? result = (_spec.Metadata, _spec);
            return Task.FromResult(result);
        }

        public Task<(ImportedSpec Spec, ParsedSpec Full)?> GetVersionAsync(OpenApiSpecId id, int v, CancellationToken ct)
        {
            GetVersionCalledWith = v;
            (ImportedSpec, ParsedSpec)? result = (_spec.Metadata, _spec);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<ImportedSpec>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportedSpec>>(Array.Empty<ImportedSpec>());

        public Task<IReadOnlyList<ImportedSpec>> GetVersionsAsync(OpenApiSpecId id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportedSpec>>(Array.Empty<ImportedSpec>());

        public Task<ApiOperation?> GetOperationAsync(OpenApiSpecId id, string operationId, CancellationToken ct) =>
            Task.FromResult<ApiOperation?>(null);

        public Task<bool> DeleteAsync(OpenApiSpecId id, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class FakeServerConfigStore : IServerConfigStore
    {
        private readonly ServerConfigInfo? _config;
        public FakeServerConfigStore(ServerConfigInfo? config) => _config = config;

        public Task<ServerConfigInfo> CreateAsync(ServerConfigInfo c, CancellationToken ct) => Task.FromResult(c);
        public Task<ServerConfigInfo> UpdateAsync(ServerConfigInfo c, CancellationToken ct) => Task.FromResult(c);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<ServerConfigInfo?> GetAsync(string id, CancellationToken ct) => Task.FromResult(_config);
        public Task<IReadOnlyList<ServerConfigInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServerConfigInfo>>(Array.Empty<ServerConfigInfo>());
    }

    private sealed class FakeCredentialAccessor : ICredentialAccessor
    {
        private readonly Dictionary<string, string> _creds;
        public FakeCredentialAccessor(Dictionary<string, string> creds) => _creds = creds;

        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken ct = default)
        {
            _creds.TryGetValue(credentialRef, out var val);
            return Task.FromResult<string?>(val);
        }
    }

    private sealed class FakeHttpClient : IHttpClient
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public FakeHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request);
    }

    private sealed class FakeNodeContext : INodeContext
    {
        public ILogger Logger { get; init; } = NullLogger.Instance;
        public IWorkflowState State { get; init; } = null!;
        public IHttpClient? Http { get; init; }
        public ICredentialAccessor? Credentials { get; init; }
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel l, EventId id, TState s, Exception? e, Func<TState, Exception?, string> f) { }
    }

    // Mirrors what DynamicCustomNodeTask injects for Interpreted nodes: a reserved __specId.
    // The FakeSpecStore ignores the id, so any non-empty value resolves the configured spec.
    private static NodeInput MakeInput(params (string Key, string Value)[] entries)
    {
        var dict = new Dictionary<string, JsonElement>
        {
            [OpenApiInterpreterExecutor.SpecIdInputKey] = JsonSerializer.SerializeToElement("test-spec")
        };
        foreach (var (k, v) in entries)
            dict[k] = JsonSerializer.SerializeToElement(v);
        return new NodeInput(dict);
    }

    private static string ArgsJson(object obj) => JsonSerializer.Serialize(obj);

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Execute_GetOperation_BuildsCorrectUrl()
    {
        var spec        = BuildSpec("api1", pathTemplate: "/pets/{id}");
        var serverCfg   = MakeServerConfig(baseUrl: "https://api.example.com");
        var capturedUrl = "";

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(req =>
            {
                capturedUrl = req.RequestUri!.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input  = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"),
            ("arguments", ArgsJson(new { path = new { id = "42" } })));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("success", result.OutputName);
        Assert.EndsWith("/pets/42", capturedUrl);
    }

    [Fact]
    public async Task Execute_ResourceLocatorPathArg_UsesStableKeyNotLabel()
    {
        var spec        = BuildSpec("api1", pathTemplate: "/pets/{id}");
        var serverCfg   = MakeServerConfig(baseUrl: "https://api.example.com");
        var capturedUrl = "";

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(req =>
            {
                capturedUrl = req.RequestUri!.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        // The path arg is a persisted resource-locator selection: { value, label, mode }.
        var args = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = new Dictionary<string, object>
            {
                ["id"] = new Dictionary<string, object> { ["value"] = "res_7f3a", ["label"] = "Fluffy", ["mode"] = "list" }
            }
        });
        var input  = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"), ("arguments", args));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("success", result.OutputName);
        Assert.EndsWith("/pets/res_7f3a", capturedUrl);
        Assert.DoesNotContain("Fluffy", capturedUrl);
    }

    [Fact]
    public async Task Execute_QueryArgs_AppendedToUrl()
    {
        var spec      = BuildSpec("api2", pathTemplate: "/pets");
        var serverCfg = MakeServerConfig(baseUrl: "https://api.example.com");
        var capturedUrl = "";

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(req =>
            {
                capturedUrl = req.RequestUri!.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"),
            ("arguments", ArgsJson(new { query = new { status = "available" } })));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.Contains("status=available", capturedUrl);
    }

    [Fact]
    public async Task Execute_HeaderArgs_AddedToRequest()
    {
        var spec        = BuildSpec("api3", pathTemplate: "/items");
        var serverCfg   = MakeServerConfig(baseUrl: "https://api.example.com");
        IEnumerable<string>? capturedHeader = null;

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(req =>
            {
                req.Headers.TryGetValues("X-Custom", out capturedHeader);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"),
            ("arguments", ArgsJson(new { header = new { @XCustom = "val" } })));
        // Use actual header name key
        var args = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["header"] = new Dictionary<string, string> { ["X-Custom"] = "val" }
        });
        input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"), ("arguments", args));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.NotNull(capturedHeader);
        Assert.Contains("val", capturedHeader!);
    }

    [Fact]
    public async Task Execute_PostWithBody_SetsContentType()
    {
        var spec = BuildSpec("api4", method: "POST", pathTemplate: "/pets",
            requestBody: new ApiRequestBody(true, new[] { "application/json" }, null!));
        var serverCfg       = MakeServerConfig(baseUrl: "https://api.example.com");
        string? contentType = null;

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(req =>
            {
                contentType = req.Content?.Headers.ContentType?.MediaType;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var args  = JsonSerializer.Serialize(new Dictionary<string, object>
            { ["body"] = new Dictionary<string, string> { ["name"] = "Fido" } });
        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"), ("arguments", args));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("application/json", contentType);
    }

    [Fact]
    public async Task Execute_OmittedOptionalArg_NotSentAsQuery()
    {
        var spec      = BuildSpec("api5", pathTemplate: "/pets");
        var serverCfg = MakeServerConfig(baseUrl: "https://api.example.com");
        var capturedUrl = "";

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(req =>
            {
                capturedUrl = req.RequestUri!.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.DoesNotContain("?", capturedUrl);
    }

    [Fact]
    public async Task Execute_Auth_ApiKeyHeader_InjectsHeader()
    {
        var schemes   = new[] { new SecurityScheme("apiKeyScheme", "apiKey", null, "header", "X-API-Key", null) };
        var spec      = BuildSpec("api6", securitySchemes: schemes);
        var serverCfg = MakeServerConfig(securityType: "apiKey", credentialRef: "key1");
        IEnumerable<string>? capturedKey = null;

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Credentials = new FakeCredentialAccessor(new Dictionary<string, string> { ["key1"] = "secret123" }),
            Http = new FakeHttpClient(req =>
            {
                req.Headers.TryGetValues("X-API-Key", out capturedKey);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.NotNull(capturedKey);
        Assert.Contains("secret123", capturedKey!);
    }

    [Fact]
    public async Task Execute_Auth_ApiKeyQuery_InjectsQueryParam()
    {
        var schemes   = new[] { new SecurityScheme("apiKeyScheme", "apiKey", null, "query", "api_key", null) };
        var spec      = BuildSpec("api7", securitySchemes: schemes);
        var serverCfg = MakeServerConfig(securityType: "apiKey", credentialRef: "key1");
        var capturedUrl = "";

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Credentials = new FakeCredentialAccessor(new Dictionary<string, string> { ["key1"] = "mysecret" }),
            Http = new FakeHttpClient(req =>
            {
                capturedUrl = req.RequestUri!.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.Contains("api_key=mysecret", capturedUrl);
    }

    [Fact]
    public async Task Execute_Auth_Bearer_InjectsAuthHeader()
    {
        var spec      = BuildSpec("api8");
        var serverCfg = MakeServerConfig(securityType: "http_bearer", credentialRef: "tok1");
        string? authHeader = null;

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Credentials = new FakeCredentialAccessor(new Dictionary<string, string> { ["tok1"] = "mytoken" }),
            Http = new FakeHttpClient(req =>
            {
                authHeader = req.Headers.Authorization?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("Bearer mytoken", authHeader);
    }

    [Fact]
    public async Task Execute_Auth_Basic_InjectsAuthHeader()
    {
        var spec      = BuildSpec("api9");
        var serverCfg = MakeServerConfig(securityType: "http_basic", credentialRef: "cred1");
        string? authHeader = null;

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Credentials = new FakeCredentialAccessor(new Dictionary<string, string> { ["cred1"] = "alice:s3cr3t" }),
            Http = new FakeHttpClient(req =>
            {
                authHeader = req.Headers.Authorization?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        await executor.ExecuteAsync(input, ctx, default);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cr3t"));
        Assert.Equal(expected, authHeader);
    }

    [Fact]
    public async Task Execute_Auth_Basic_PasswordWithColon_SplitsOnFirstColon()
    {
        var spec      = BuildSpec("api10");
        var serverCfg = MakeServerConfig(securityType: "http_basic", credentialRef: "cred1");
        string? authHeader = null;

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Credentials = new FakeCredentialAccessor(new Dictionary<string, string> { ["cred1"] = "user:p:ass" }),
            Http = new FakeHttpClient(req =>
            {
                authHeader = req.Headers.Authorization?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        await executor.ExecuteAsync(input, ctx, default);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:p:ass"));
        Assert.Equal(expected, authHeader);
    }

    [Fact]
    public async Task Execute_ServerVariables_SubstitutedInBaseUrl()
    {
        var spec      = BuildSpec("api11", pathTemplate: "/v1/resource");
        var serverCfg = MakeServerConfig(
            baseUrl: "https://{env}.api.com",
            serverVariables: new Dictionary<string, string> { ["env"] = "prod" });
        var capturedUrl = "";

        var executor = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(req =>
            {
                capturedUrl = req.RequestUri!.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.StartsWith("https://prod.api.com", capturedUrl);
    }

    [Fact]
    public async Task Execute_SpecVersion_Pinned_LoadsCorrectVersion()
    {
        var spec      = BuildSpec("api12");
        var serverCfg = MakeServerConfig();
        var fakeStore = new FakeSpecStore(spec);

        var executor = CompileAndInstantiate(spec, fakeStore, new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") }))
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"),
            ("specVersion", "2"));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal(2, fakeStore.GetVersionCalledWith);
    }

    [Fact]
    public async Task Execute_SpecVersion_NotSet_LoadsLatest()
    {
        var spec      = BuildSpec("api13");
        var serverCfg = MakeServerConfig();
        var fakeStore = new FakeSpecStore(spec);

        var executor = CompileAndInstantiate(spec, fakeStore, new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") }))
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        await executor.ExecuteAsync(input, ctx, default);

        Assert.True(fakeStore.GetLatestCalled);
    }

    [Fact]
    public async Task Execute_SuccessResponse_ReturnsSuccessOutput()
    {
        var spec      = BuildSpec("api14");
        var serverCfg = MakeServerConfig();
        var executor  = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"ok\":true}") }))
        };

        var input  = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("success", result.OutputName);
        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Execute_ErrorResponse_ReturnsErrorOutput()
    {
        var spec      = BuildSpec("api15");
        var serverCfg = MakeServerConfig();
        var executor  = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    { Content = new StringContent("bad request") }))
        };

        var input  = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("error", result.OutputName);
        // HTTP error response routes to the "error" port; the node itself succeeded.
        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Execute_MissingSpecId_ReturnsError()
    {
        var spec      = BuildSpec("api-nospec");
        var serverCfg = MakeServerConfig();
        var executor  = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext { Http = new FakeHttpClient(_ => throw new Exception("should not be called")) };
        // Build input WITHOUT the reserved __specId — the dispatcher failed to inject it.
        var input = new NodeInput(new Dictionary<string, JsonElement>
        {
            ["operationId"]    = JsonSerializer.SerializeToElement("getItem"),
            ["serverConfigId"] = JsonSerializer.SerializeToElement("srv1"),
        });
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("error", result.OutputName);
        Assert.Equal(NodeExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Execute_SpecIdProvided_ResolvesSpecAndRuns()
    {
        var spec      = BuildSpec("api-withspec");
        var serverCfg = MakeServerConfig();
        var fakeStore = new FakeSpecStore(spec);
        var executor  = CompileAndInstantiate(spec, fakeStore, new FakeServerConfigStore(serverCfg));

        var ctx = new FakeNodeContext
        {
            Http = new FakeHttpClient(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") }))
        };

        var input  = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.True(fakeStore.GetLatestCalled);
        Assert.Equal("success", result.OutputName);
    }

    [Fact]
    public async Task Execute_MissingOperationId_ReturnsError()
    {
        var spec      = BuildSpec("api16");
        var serverCfg = MakeServerConfig();
        var executor  = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg));

        var ctx   = new FakeNodeContext { Http = new FakeHttpClient(_ => throw new Exception("should not be called")) };
        var input = MakeInput(("serverConfigId", "srv1"));  // no operationId
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("error", result.OutputName);
    }

    [Fact]
    public async Task Execute_MissingServerConfig_ReturnsError()
    {
        var spec      = BuildSpec("api17");
        var executor  = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(null));  // null = not found

        var ctx   = new FakeNodeContext { Http = new FakeHttpClient(_ => throw new Exception("should not be called")) };
        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "unknown"));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("error", result.OutputName);
    }

    // -------------------------------------------------------------------------
    // OAuth2 tests
    // -------------------------------------------------------------------------

    private sealed class FakeOAuthTokenCache : IOAuthTokenCache
    {
        private readonly Queue<string> _tokens;
        public int GetCallCount { get; private set; }
        public int InvalidateCallCount { get; private set; }

        public FakeOAuthTokenCache(params string[] tokens) =>
            _tokens = new Queue<string>(tokens);

        public Task<string> GetTokenAsync(
            string cacheKey, string tokenUrl, string clientId, string clientSecret,
            IReadOnlyList<string> scopes, CancellationToken ct = default)
        {
            GetCallCount++;
            return Task.FromResult(_tokens.Count > 0 ? _tokens.Dequeue() : "fallback-token");
        }

        public void Invalidate(string cacheKey) => InvalidateCallCount++;
    }

    [Fact]
    public async Task Execute_Auth_OAuth2_InjectsBearerToken()
    {
        var schemes   = new[] { new SecurityScheme("oauth2Scheme", "oauth2", null, null, null, "https://auth.test/token") };
        var spec      = BuildSpec("oauth-api1", securitySchemes: schemes);
        var serverCfg = MakeServerConfig(securityType: "oauth2", credentialRef: "cred1");
        string? authHeader = null;

        var fakeCache = new FakeOAuthTokenCache("my-oauth-token");
        var executor  = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg), fakeCache);

        var ctx = new FakeNodeContext
        {
            Credentials = new FakeCredentialAccessor(new Dictionary<string, string> { ["cred1"] = "clientId:clientSecret" }),
            Http = new FakeHttpClient(req =>
            {
                authHeader = req.Headers.Authorization?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("ok") });
            })
        };

        var input = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("success", result.OutputName);
        Assert.Equal("Bearer my-oauth-token", authHeader);
    }

    [Fact]
    public async Task Execute_Auth_OAuth2_On401_Retries()
    {
        var schemes   = new[] { new SecurityScheme("oauth2Scheme", "oauth2", null, null, null, "https://auth.test/token") };
        var spec      = BuildSpec("oauth-api2", securitySchemes: schemes);
        var serverCfg = MakeServerConfig(securityType: "oauth2", credentialRef: "cred1");

        // First call returns 401, second call returns 200
        var callCount = 0;
        var fakeCache = new FakeOAuthTokenCache("stale-token", "fresh-token");
        var executor  = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg), fakeCache);

        string? lastAuthHeader = null;
        var ctx = new FakeNodeContext
        {
            Credentials = new FakeCredentialAccessor(new Dictionary<string, string> { ["cred1"] = "cid:csec" }),
            Http = new FakeHttpClient(req =>
            {
                lastAuthHeader = req.Headers.Authorization?.ToString();
                callCount++;
                var status = callCount == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
                return Task.FromResult(new HttpResponseMessage(status)
                    { Content = new StringContent(callCount == 1 ? "unauthorized" : "ok") });
            })
        };

        var input  = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("success", result.OutputName);
        Assert.Equal(2, callCount);
        Assert.Equal(1, fakeCache.InvalidateCallCount);
        Assert.Equal("Bearer fresh-token", lastAuthHeader);
    }

    [Fact]
    public async Task Execute_Auth_OAuth2_On401_RetriesOnlyOnce()
    {
        var schemes   = new[] { new SecurityScheme("oauth2Scheme", "oauth2", null, null, null, "https://auth.test/token") };
        var spec      = BuildSpec("oauth-api3", securitySchemes: schemes);
        var serverCfg = MakeServerConfig(securityType: "oauth2", credentialRef: "cred1");

        // Both calls return 401
        var fakeCache = new FakeOAuthTokenCache("tok1", "tok2");
        var executor  = CompileAndInstantiate(spec,
            new FakeSpecStore(spec), new FakeServerConfigStore(serverCfg), fakeCache);

        var ctx = new FakeNodeContext
        {
            Credentials = new FakeCredentialAccessor(new Dictionary<string, string> { ["cred1"] = "cid:csec" }),
            Http = new FakeHttpClient(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    { Content = new StringContent("unauthorized") }))
        };

        var input  = MakeInput(("operationId", "getItem"), ("serverConfigId", "srv1"));
        var result = await executor.ExecuteAsync(input, ctx, default);

        Assert.Equal("error", result.OutputName);
        // Both retry calls return 401 — routes to "error" port, node still succeeded as an execution.
        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public void DynamicCustomNodeTask_ExistingExecutors_StillUseParameterlessCtor()
    {
        // Verify that a class with no matching ctor falls back to Activator.CreateInstance
        const string code = @"
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace TestNs;
public class SimpleExecutor : INodeExecutor
{
    public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext ctx, CancellationToken ct)
        => new ValueTask<NodeResult>(new NodeResult(""success"", null, NodeExecutionStatus.Succeeded));
}";
        var refs = BuildReferences();
        var tree = CSharpSyntaxTree.ParseText(code);
        var comp = CSharpCompilation.Create("Simple", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emitResult = comp.Emit(ms);
        Assert.True(emitResult.Success);

        var ctx = new CollectibleAssemblyLoadContext("Simple");
        var asm  = ctx.LoadFromBytes(ms.ToArray());
        var type = asm.GetTypes().First(t => typeof(INodeExecutor).IsAssignableFrom(t) && !t.IsAbstract);

        // No matching ctor → parameterless fallback
        var knownServices = new Dictionary<Type, object?>
        {
            [typeof(IOpenApiSpecStore)]  = (object?)null,
            [typeof(IServerConfigStore)] = (object?)null,
            [typeof(IOAuthTokenCache)]   = (object?)null,
        };
        var matchedCtor = type.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().All(p => knownServices.ContainsKey(p.ParameterType)));

        var executor = matchedCtor != null
            ? (INodeExecutor)matchedCtor.Invoke(
                matchedCtor.GetParameters().Select(p => knownServices[p.ParameterType]).ToArray())
            : (INodeExecutor)Activator.CreateInstance(type)!;

        Assert.NotNull(executor);
        Assert.IsAssignableFrom<INodeExecutor>(executor);
    }
}
