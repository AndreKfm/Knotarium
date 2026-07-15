using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts.Ai;

/// <summary>
/// One agent tool invocation: run the target workflow as a seeded child run of the parent agent execution.
/// The tool binding (target workflow, declared outputs) is authoritative and comes from the node; the model
/// only supplies <see cref="Arguments"/>, already schema-validated by the node before it reaches here.
/// </summary>
/// <param name="ParentExecutionId">The agent node's own execution instance id (for the back-link + recursion guard).</param>
/// <param name="ParentNodeId">The agent node id within the parent workflow (for the back-link).</param>
/// <param name="Iteration">1-based loop iteration that issued this call (for observability).</param>
/// <param name="ToolName">The tool name the model used (for observability + error messages).</param>
/// <param name="WorkflowId">The target workflow definition id to run as the tool.</param>
/// <param name="Arguments">Validated arguments; each becomes a seeded global on the child run.</param>
/// <param name="Outputs">Global-variable names projected out of the finished child run as the tool result.</param>
public sealed record AgentToolInvocation(
    Guid ParentExecutionId,
    string ParentNodeId,
    int Iteration,
    string ToolName,
    string WorkflowId,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyList<string> Outputs);

/// <summary>
/// The result of a tool invocation as it will be handed back to the model. On success <see cref="ResultJson"/>
/// is the projected <c>outputs</c> object; on failure it is a sanitized error object (the loop continues either
/// way — a failed tool is not an agent-node failure). <see cref="ChildExecutionId"/> links to the real child run.
/// </summary>
public sealed record AgentToolResult(
    bool Success,
    string ResultJson,
    Guid ChildExecutionId,
    string? Error = null);

/// <summary>
/// Runs an agent tool (an existing workflow) as an in-process, journaled child run of the parent agent
/// execution, then projects the requested outputs back as the tool result. Lives in Core as a seam because
/// only the Execution slice may create/drive <c>ExecutionInstance</c>s; the node consumes this contract and
/// never touches execution internals. Implementations enforce the structural guards (recursion depth = 1,
/// tool workflow must be enabled + published with a plain start entry) and never throw for an ordinary tool
/// failure — that is reported via <see cref="AgentToolResult.Success"/> = false so the loop can react.
/// </summary>
public interface IAgentToolRunner
{
    Task<AgentToolResult> RunToolAsync(AgentToolInvocation invocation, CancellationToken cancellationToken = default);
}
