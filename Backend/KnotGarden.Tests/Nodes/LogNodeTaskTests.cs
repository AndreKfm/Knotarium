using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Nodes;
using Xunit;

namespace KnotGarden.Tests.Nodes;

public class LogNodeTaskTests
{
    private class FakeLogger<T> : ILogger<T>
    {
        public List<string> LoggedMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task LogNodeTask_WritesStructuredLogMessage()
    {
        // Arrange
        var logger = new FakeLogger<LogNodeTask>();
        var task = new LogNodeTask(logger);

        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("log-1"),
            Inputs: new Dictionary<string, object>
            {
                ["message"] = "Test log output"
            },
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Single(logger.LoggedMessages);
        Assert.Contains("Test log output", logger.LoggedMessages[0]);
    }
}
