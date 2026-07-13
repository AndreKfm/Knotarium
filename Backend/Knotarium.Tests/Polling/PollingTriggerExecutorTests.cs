using System.Collections.Generic;
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

        var outputs = TriggerEntryResolver.CreateTriggerOutputs("pollingTrigger", instance);

        Assert.True(outputs.ContainsKey("result"));
        Assert.Equal("{\"v\":1}", outputs["result"]);
    }

    [Fact]
    public void CreateTriggerOutputs_PollingTrigger_NoPayload_EmitsNothing()
    {
        var instance = new ExecutionInstance
        {
            Id = ExecutionInstanceId.New(),
            WorkflowDefinitionId = new WorkflowDefinitionId("wf-1"),
            TriggerOrigin = "poll",
            GlobalVariables = new Dictionary<string, object>()
        };

        var outputs = TriggerEntryResolver.CreateTriggerOutputs("pollingTrigger", instance);

        Assert.False(outputs.ContainsKey("result"));
    }

    [Fact]
    public void IsTriggerCompatibleWithOrigin_PollMapsToPollingTrigger()
    {
        Assert.True(TriggerEntryResolver.IsTriggerCompatibleWithOrigin("pollingTrigger", "poll"));
        Assert.False(TriggerEntryResolver.IsTriggerCompatibleWithOrigin("scheduler", "poll"));
    }
}
