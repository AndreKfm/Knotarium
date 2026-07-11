namespace KnotGarden.Core.Domain;

public enum NodeStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Waiting,
    RequiresManualDecision
}
