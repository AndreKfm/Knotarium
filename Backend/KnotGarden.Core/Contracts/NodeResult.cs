using System.Text.Json;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts;

public sealed record NodeResult(
    string OutputName,
    JsonElement? Payload,
    NodeExecutionStatus Status);
