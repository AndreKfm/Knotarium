using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Features.NodeEditor;

public sealed record NodeEditorTestRequest(
    string PackageId,
    string ManifestYaml,
    string ExecutorCode,
    string TestsYaml
);

public sealed record NodeEditorTestCaseResult(
    string Name,
    string Status,
    string Message
);

public sealed record NodeEditorTestResponse(
    bool Success,
    List<string> Logs,
    List<NodeEditorTestCaseResult> Cases
);

public interface INodeEditorSessionGate
{
    void MarkPassed(string packageId, string version);
    bool HasPassingResult(string packageId, string version);
}

public interface INodeEditorSandboxService
{
    Task<NodeEditorTestResponse> RunTestsAsync(NodeEditorTestRequest request, CancellationToken cancellationToken);
}
