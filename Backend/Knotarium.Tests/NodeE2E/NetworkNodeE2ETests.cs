// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using MQTTnet;
using MQTTnet.Server;
using Xunit;

namespace Knotarium.Tests.NodeE2E;

/// <summary>
/// Real-node-through-real-engine e2e for the messaging nodes.
///
/// <para><b>mqPublish</b> gets a full happy-path double: an in-process MQTTnet broker on a loopback port,
/// so the shipped node actually connects and publishes and we assert the broker received the message.</para>
///
/// <para><b>smtpSend / imapFetch</b> use MailKit against a real server and have no reliable in-process
/// double (hosting SMTP/IMAP servers in a unit test is heavy and flaky, and message construction is already
/// covered by <c>EmailNodeTaskTests</c>). Here we drive them through the engine against a closed loopback
/// port and assert graceful failure — proving the node is registered, reads its config, executes, and
/// surfaces connection errors through the executor. The live send/fetch path is left to manual integration.</para>
/// </summary>
[Collection(WorkflowExecutionIsolationCollection.Name)]
public class NetworkNodeE2ETests
{
    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task MqPublish_publishes_the_payload_to_an_in_process_broker()
    {
        var port = FreeLoopbackPort();
        var factory = new MqttFactory();
        using var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .Build());

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.InterceptingPublishAsync += args =>
        {
            if (args.ApplicationMessage.Topic == "e2e/topic")
            {
                received.TrySetResult(Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment));
            }
            return Task.CompletedTask;
        };

        await broker.StartAsync();
        try
        {
            using var harness = new NodeE2EHarness();

            var run = await harness.RunNodeAsync("mqPublish", new Dictionary<string, object>
            {
                ["host"] = "127.0.0.1",
                ["port"] = port,
                ["topic"] = "e2e/topic",
                ["payload"] = "hello mqtt",
                ["qos"] = 0,
            });

            Assert.Equal(ExecutionStatus.Completed, run.Status);
            Assert.Equal(NodeStatus.Completed, run.Node.Status);

            var deliveredWithin = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(received.Task, deliveredWithin);
            Assert.Equal("hello mqtt", await received.Task);
        }
        finally
        {
            await broker.StopAsync();
        }
    }

    [Fact]
    public async Task MqPublish_fails_gracefully_when_the_broker_is_unreachable()
    {
        var closedPort = FreeLoopbackPort(); // nothing is listening here
        using var harness = new NodeE2EHarness();

        var run = await harness.RunNodeAsync("mqPublish", new Dictionary<string, object>
        {
            ["host"] = "127.0.0.1",
            ["port"] = closedPort,
            ["topic"] = "e2e/topic",
            ["payload"] = "x",
        });

        Assert.NotEqual(ExecutionStatus.Completed, run.Status);
        Assert.NotEqual(NodeStatus.Completed, run.Node.Status);
    }

    [Fact]
    public async Task SmtpSend_fails_gracefully_when_the_server_is_unreachable()
    {
        var closedPort = FreeLoopbackPort();
        using var harness = new NodeE2EHarness();

        var run = await harness.RunNodeAsync("smtpSend", new Dictionary<string, object>
        {
            ["host"] = "127.0.0.1",
            ["port"] = closedPort,
            ["security"] = "none",
            ["from"] = "sender@example.com",
            ["to"] = "recipient@example.com",
            ["subject"] = "e2e",
            ["body"] = "hello",
        });

        // Registered, resolved, executed, and surfaced the connection error through the engine.
        Assert.NotEqual(ExecutionStatus.Completed, run.Status);
        Assert.NotEqual(NodeStatus.Completed, run.Node.Status);
    }

    [Fact]
    public async Task ImapFetch_fails_gracefully_when_the_server_is_unreachable()
    {
        var closedPort = FreeLoopbackPort();
        using var harness = new NodeE2EHarness();

        var run = await harness.RunNodeAsync("imapFetch", new Dictionary<string, object>
        {
            ["host"] = "127.0.0.1",
            ["port"] = closedPort,
            ["security"] = "none",
            ["username"] = "user@example.com",
            ["credentialRef"] = "imap-pass",
        });

        Assert.NotEqual(ExecutionStatus.Completed, run.Status);
        Assert.NotEqual(NodeStatus.Completed, run.Node.Status);
    }
}
