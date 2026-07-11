namespace Knotarium.Core.Domain;

public enum NodeTier
{
    Declarative,
    Compiled,
    Interpreted
}

public enum NodeSideEffectKind
{
    IdempotentSideEffect,
    NonIdempotentSideEffect
}

public enum RecoveryMode
{
    RetryAutomatically,
    FailImmediately
}

public enum NodeExecutionStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Retrying = 4,
    RequiresManualDecision = 5,
    TimedOut = 6,
    Cancelled = 7
}
