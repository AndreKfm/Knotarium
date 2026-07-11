using System.Collections.Generic;
using System.Reflection;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Execution;
using KnotGarden.Features.Polling;
using Xunit;

namespace KnotGarden.Tests.Polling;

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
            "CreateTriggerOutputs", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var outputs = (Dictionary<string, object>)method!.Invoke(null, new object[] { "pollingTrigger", instance })!;

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

        var method = typeof(WorkflowExecutor).GetMethod(
            "CreateTriggerOutputs", BindingFlags.NonPublic | BindingFlags.Static);
        var outputs = (Dictionary<string, object>)method!.Invoke(null, new object[] { "pollingTrigger", instance })!;

        Assert.False(outputs.ContainsKey("result"));
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
