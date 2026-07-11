using System;

namespace Knotarium.Core.Domain;

public sealed class CorrelationToken
{
    public Guid Id { get; set; }
    public string HashedToken { get; set; } = null!;
    public ExecutionInstanceId ExecutionInstanceId { get; set; }
    public NodeId NodeId { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}
