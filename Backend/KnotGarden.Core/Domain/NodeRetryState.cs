using System;

namespace KnotGarden.Core.Domain;

public sealed class NodeRetryState
{
    public Guid Id { get; set; }
    public ExecutionInstanceId ExecutionInstanceId { get; set; }
    public NodeId NodeId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset NextRetryAtUtc { get; set; }
    public string SanitizedFailureMessage { get; set; } = null!;
}
