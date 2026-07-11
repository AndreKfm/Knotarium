using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Notifications;
using Xunit;

namespace Knotarium.Tests.Notifications;

public class NotificationTests
{
    private static NotificationChannel Channel(string id, NotificationChannelType type = NotificationChannelType.Webhook, bool isDefault = false)
        => new()
        {
            Id = id,
            Name = $"Channel {id}",
            Type = type,
            EncryptedConfig = "{}",
            IsDefaultFailureAlert = isDefault,
        };

    private static NotificationMessage SampleMessage() => new FailureAlertMessage(
        WorkflowName: "Nightly Sync",
        WorkflowId: "wf-1",
        ExecutionId: "exec-1",
        FailedNodeId: "node-3",
        ErrorMessage: "boom",
        TriggerOrigin: "schedule",
        TimestampUtc: DateTimeOffset.UnixEpoch).ToNotification();

    // --- Channel resolution ---

    [Fact]
    public void Resolve_InheritUsesDefaultChannels()
    {
        var all = new[] { Channel("a", isDefault: true), Channel("b"), Channel("c", isDefault: true) };

        var resolved = FailureAlertChannelResolver.Resolve(new FailureAlertConfig(FailureAlertModes.Inherit), all);

        Assert.Equal(new[] { "a", "c" }, resolved.Select(c => c.Id));
    }

    [Fact]
    public void Resolve_NullConfigDefaultsToInherit()
    {
        var all = new[] { Channel("a", isDefault: true), Channel("b") };

        var resolved = FailureAlertChannelResolver.Resolve(null, all);

        Assert.Equal(new[] { "a" }, resolved.Select(c => c.Id));
    }

    [Fact]
    public void Resolve_OffReturnsNoChannels()
    {
        var all = new[] { Channel("a", isDefault: true) };

        var resolved = FailureAlertChannelResolver.Resolve(new FailureAlertConfig(FailureAlertModes.Off), all);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Resolve_CustomReturnsOnlySelectedChannels()
    {
        var all = new[] { Channel("a", isDefault: true), Channel("b"), Channel("c") };

        var resolved = FailureAlertChannelResolver.Resolve(
            new FailureAlertConfig(FailureAlertModes.Custom, new[] { "b", "c" }),
            all);

        Assert.Equal(new[] { "b", "c" }, resolved.Select(c => c.Id));
    }

    [Fact]
    public void Resolve_CustomWithNoIdsReturnsNothing()
    {
        var all = new[] { Channel("a", isDefault: true) };

        var resolved = FailureAlertChannelResolver.Resolve(new FailureAlertConfig(FailureAlertModes.Custom, null), all);

        Assert.Empty(resolved);
    }

    // --- Webhook sender payload ---

    [Fact]
    public async Task WebhookSender_PostsExpectedJsonToConfiguredUrl()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            captured = req;
            body = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var sender = new WebhookNotificationSender(new FakeHttpClientFactory(new HttpClient(handler)));

        using var config = JsonDocument.Parse("{\"url\":\"https://example.com/hook\"}");
        await sender.SendAsync(config.RootElement, SampleMessage(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://example.com/hook", captured.RequestUri?.AbsoluteUri);
        Assert.NotNull(body);
        using var parsed = JsonDocument.Parse(body!);
        Assert.Equal("workflow.failed", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal("wf-1", parsed.RootElement.GetProperty("workflowId").GetString());
        Assert.Equal("node-3", parsed.RootElement.GetProperty("failedNodeId").GetString());
        Assert.Equal("boom", parsed.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WebhookSender_ThrowsWhenUrlMissing()
    {
        var sender = new WebhookNotificationSender(new FakeHttpClientFactory(new HttpClient(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))));

        using var config = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(config.RootElement, SampleMessage(), CancellationToken.None));
    }

    // --- Dispatcher routing ---

    [Fact]
    public async Task Dispatcher_DecryptsConfigAndRoutesToMatchingSender()
    {
        var slack = new RecordingSender(NotificationChannelType.Slack);
        var webhook = new RecordingSender(NotificationChannelType.Webhook);
        var dispatcher = new NotificationDispatcher(new INotificationSender[] { slack, webhook }, new IdentityCipher());

        var channel = new NotificationChannel
        {
            Id = "s1",
            Name = "Slack",
            Type = NotificationChannelType.Slack,
            EncryptedConfig = "{\"webhookUrl\":\"https://hooks.slack.com/x\"}",
        };

        await dispatcher.SendAsync(channel, SampleMessage(), CancellationToken.None);

        Assert.Equal(1, slack.CallCount);
        Assert.Equal(0, webhook.CallCount);
        Assert.Equal("https://hooks.slack.com/x", NotificationConfigHelper.GetWebhookUrl(slack.LastConfig));
    }

    [Fact]
    public async Task Dispatcher_ThrowsWhenNoSenderRegisteredForType()
    {
        var dispatcher = new NotificationDispatcher(
            new INotificationSender[] { new RecordingSender(NotificationChannelType.Webhook) },
            new IdentityCipher());

        var channel = new NotificationChannel { Id = "e1", Name = "Email", Type = NotificationChannelType.Email, EncryptedConfig = "{}" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(channel, SampleMessage(), CancellationToken.None));
    }

    // --- Test doubles ---

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;
        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) => _sendAsync = sendAsync;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _sendAsync(request, cancellationToken);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class IdentityCipher : ICredentialCipher
    {
        public string Encrypt(string plainText) => plainText;
        public string Decrypt(string cipherText) => cipherText;
    }

    private sealed class RecordingSender : INotificationSender
    {
        public RecordingSender(NotificationChannelType type) => Type = type;
        public NotificationChannelType Type { get; }
        public int CallCount { get; private set; }
        public JsonElement LastConfig { get; private set; }

        public Task SendAsync(JsonElement config, NotificationMessage message, CancellationToken cancellationToken)
        {
            CallCount++;
            // Clone so the value survives after the caller disposes the JsonDocument.
            LastConfig = config.Clone();
            return Task.CompletedTask;
        }
    }

    private static class NotificationConfigHelper
    {
        public static string? GetWebhookUrl(JsonElement config)
            => config.ValueKind == JsonValueKind.Object && config.TryGetProperty("webhookUrl", out var v) ? v.GetString() : null;
    }
}
