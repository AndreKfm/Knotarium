using System;

namespace KnotGarden.Core.Domain;

public class AuditEntry
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Details { get; set; } = string.Empty;
    public string PreviousHash { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
}
