using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Nodes;
using MQTTnet.Protocol;
using Xunit;

namespace KnotGarden.Tests.Nodes;

/// <summary>
/// Unit coverage for the testable seam of the MQTT publish node — message construction. The live
/// connect/publish path requires a broker and is exercised by manual integration.
/// </summary>
public class MqPublishNodeTaskTests
{
    [Fact]
    public void BuildMessage_sets_topic_payload_qos_and_retain()
    {
        var message = MqPublishNodeTask.BuildMessage("sensors/temp", "21.5", qos: 1, retain: true);

        Assert.Equal("sensors/temp", message.Topic);
        Assert.Equal("21.5", Encoding.UTF8.GetString(message.PayloadSegment.ToArray()));
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, message.QualityOfServiceLevel);
        Assert.True(message.Retain);
    }

    [Fact]
    public void BuildMessage_clamps_out_of_range_qos()
    {
        Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, MqPublishNodeTask.BuildMessage("t", "p", qos: 9, retain: false).QualityOfServiceLevel);
        Assert.Equal(MqttQualityOfServiceLevel.AtMostOnce, MqPublishNodeTask.BuildMessage("t", "p", qos: -1, retain: false).QualityOfServiceLevel);
    }

    [Fact]
    public async Task Missing_host_or_topic_fails_fast()
    {
        var task = new MqPublishNodeTask(new NullSecretResolver());
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("mq-1"),
            Inputs: new Dictionary<string, object> { ["payload"] = "x" },
            GlobalVariables: new Dictionary<string, object>());

        Assert.IsType<LegacyNodeResult.Failure>(await task.ExecuteAsync(context, CancellationToken.None));
    }

    private sealed class NullSecretResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }
}
