namespace Knotarium.Core.Domain;

/// <summary>
/// Global-variable keys under which a trigger's inbound payload is carried on an
/// <see cref="ExecutionInstance"/> from the enqueuer that starts the run to the executor that seeds the
/// trigger node's <c>result</c> port. This is a wire contract shared between the producing slice
/// (Polling/Notifications/external-signal enqueuers) and the consuming executor, so it lives in Core
/// rather than in either slice — otherwise the executor would reach into e.g. Polling for the key.
/// </summary>
public static class TriggerPayloadKeys
{
    /// <summary>Payload from a polling trigger (<c>pollingTrigger</c>).</summary>
    public const string Poll = "__pollPayload";

    /// <summary>Failure-context payload for the global error workflow (<c>errorTrigger</c>).</summary>
    public const string Error = "__errorPayload";

    /// <summary>Normalized inbound envelope from an external Event/Action signal.</summary>
    public const string ExternalSignal = "__externalSignal";

    /// <summary>The full validated argument object an <c>aiAgent</c> node passes when invoking a workflow as a tool.</summary>
    public const string Agent = "__agentPayload";
}
