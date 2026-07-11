using System;

namespace KnotGarden.Core.Domain;

public class ActiveWorker
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset LastHeartbeat { get; set; }
}
