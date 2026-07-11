using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace KnotGarden.Features.Nodes;

/// <summary>
/// Publishes a message to an MQTT topic (MQTTnet). The password is resolved from a stored credential.
/// Message construction is factored into <see cref="BuildMessage"/> so it is unit-testable without a
/// broker. Emits <c>result = { topic, published }</c>. (The push consumer/trigger is a separate,
/// long-lived subsystem — not this node.)
/// </summary>
public class MqPublishNodeTask : INodeTask
{
    private readonly ISecretResolver _secretResolver;

    public MqPublishNodeTask(ISecretResolver secretResolver) => _secretResolver = secretResolver;

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var host = Input(context, "host");
        var topic = Input(context, "topic");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(topic))
        {
            return new LegacyNodeResult.Failure("MQTT publish failed: 'host' and 'topic' are required.");
        }

        var port = int.TryParse(Input(context, "port"), out var parsedPort) ? parsedPort : 1883;
        var clientId = Input(context, "clientId");
        var username = Input(context, "username");
        var useTls = IsTrue(context, "useTls");
        var qos = int.TryParse(Input(context, "qos"), out var parsedQos) ? parsedQos : 0;
        var retain = IsTrue(context, "retain");
        var payload = Input(context, "payload") ?? string.Empty;

        var credentialRef = Input(context, "credentialRef");
        var password = !string.IsNullOrWhiteSpace(credentialRef)
            ? await _secretResolver.ResolveAsync(credentialRef!, cancellationToken)
            : null;

        try
        {
            var factory = new MqttFactory();
            using var client = factory.CreateMqttClient();

            var optionsBuilder = new MqttClientOptionsBuilder().WithTcpServer(host, port);
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                optionsBuilder = optionsBuilder.WithClientId(clientId);
            }
            if (!string.IsNullOrEmpty(username))
            {
                optionsBuilder = optionsBuilder.WithCredentials(username, password ?? string.Empty);
            }
            if (useTls)
            {
                optionsBuilder = optionsBuilder.WithTlsOptions(o => o.UseTls());
            }

            await client.ConnectAsync(optionsBuilder.Build(), cancellationToken);
            await client.PublishAsync(BuildMessage(topic, payload, qos, retain), cancellationToken);
            await client.DisconnectAsync();

            return new LegacyNodeResult.Success(new Dictionary<string, object>
            {
                ["result"] = new Dictionary<string, object> { ["topic"] = topic, ["published"] = true },
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"MQTT publish failed: {ex.Message}");
        }
    }

    /// <summary>Builds the MQTT application message (topic + payload + QoS + retain).</summary>
    internal static MqttApplicationMessage BuildMessage(string topic, string payload, int qos, bool retain) =>
        new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(qos, 0, 2))
            .WithRetainFlag(retain)
            .Build();

    private static string? Input(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsTrue(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) && value is not null && bool.TryParse(value.ToString(), out var flag) && flag;
}
