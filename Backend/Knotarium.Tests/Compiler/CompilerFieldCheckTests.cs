using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Knotarium.Tests.Compiler;

public class CompilerFieldCheckTests
{
    // Producer emits an object output "out" with a known field schema. Consumer declares a typed
    // data input "data" whose required fields are supplied per-test. Everything else delegates to
    // the built-in InMemory provider so "start" et al resolve normally.
    private sealed class FieldManifestProvider : INodePackageManifestProvider
    {
        private readonly InMemoryNodePackageManifestProvider _inner = new();
        private readonly List<FieldSchema> _consumerRequiredFields;

        public FieldManifestProvider(List<FieldSchema> consumerRequiredFields)
            => _consumerRequiredFields = consumerRequiredFields;

        public Task<NodePackageManifest?> GetManifestAsync(NodePackageId packageId, CancellationToken cancellationToken = default)
        {
            if (packageId.Value.Equals("producer", System.StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<NodePackageManifest?>(new NodePackageManifest(
                    new NodePackageId("producer"), "1.0.0", "Producer", "Test",
                    NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately,
                    10, new List<string>(), new List<ParameterDefinition>(),
                    new List<OutputDefinition>
                    {
                        new("out", "object", new List<FieldSchema>
                        {
                            new("id", "number", true),
                            new("name", "string", true),
                            new("payload", "object", true),
                        }),
                    }));
            }

            if (packageId.Value.Equals("consumer", System.StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<NodePackageManifest?>(new NodePackageManifest(
                    new NodePackageId("consumer"), "1.0.0", "Consumer", "Test",
                    NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately,
                    10, new List<string>(), new List<ParameterDefinition>(),
                    new List<OutputDefinition> { new("result") },
                    inputs: new List<InputDefinition> { new("data", "object", _consumerRequiredFields) }));
            }

            return _inner.GetManifestAsync(packageId, cancellationToken);
        }
    }

    private static async Task<CompilationResult> CompileFlowAsync(List<FieldSchema> consumerRequiredFields)
    {
        var compiler = new WorkflowCompiler(new MockWorkflowDefinitionProvider(), new FieldManifestProvider(consumerRequiredFields));

        var start = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var producer = new NodeDefinition(NodeId.Create("producer"), "producer", new Dictionary<string, object>());
        var consumer = new NodeDefinition(NodeId.Create("consumer"), "consumer", new Dictionary<string, object>());

        var workflow = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Field-check flow",
            new[] { start, producer, consumer },
            new[]
            {
                new EdgeDefinition("e1", start.Id, "result", producer.Id, "in"),
                new EdgeDefinition("e2", producer.Id, "out", consumer.Id, "data"),
            });

        return await compiler.CompileAsync(workflow);
    }

    [Fact]
    public async Task RequiredFieldNotProvided_WarnsMissingField()
    {
        // Consumer needs "email", which the producer's output schema doesn't deliver.
        var result = await CompileFlowAsync(new List<FieldSchema>
        {
            new("email", "string", true),
            new("id", "number", true),   // provided -> fine
        });

        Assert.True(result.IsSuccess); // non-blocking
        var warning = Assert.Single(result.Diagnostics, d => d.Code == "WARN_MISSING_FIELD");
        Assert.Equal("e2", warning.EdgeId);
        Assert.Contains("email", warning.Message);
    }

    [Fact]
    public async Task RequiredFieldWrongType_WarnsFieldTypeMismatch()
    {
        // Consumer needs "payload" as a number; producer delivers it as an object.
        var result = await CompileFlowAsync(new List<FieldSchema>
        {
            new("payload", "number", true),
        });

        Assert.True(result.IsSuccess);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == "WARN_FIELD_TYPE_MISMATCH");
        Assert.Equal("e2", warning.EdgeId);
        Assert.Contains("payload", warning.Message);
    }

    [Fact]
    public async Task AllRequiredFieldsSatisfied_NoWarning()
    {
        var result = await CompileFlowAsync(new List<FieldSchema>
        {
            new("id", "number", true),
            new("name", "string", true),
        });

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "WARN_MISSING_FIELD" or "WARN_FIELD_TYPE_MISMATCH");
    }

    [Fact]
    public async Task NoRequiredFields_NoFieldChecks()
    {
        var result = await CompileFlowAsync(new List<FieldSchema>());

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "WARN_MISSING_FIELD" or "WARN_FIELD_TYPE_MISMATCH");
    }
}

public class ManifestConformanceTests
{
    private static readonly OutputDefinition Output = new("out", "object", new List<FieldSchema>
    {
        new("id", "number", true),
        new("name", "string", true),
    });

    [Fact]
    public void MatchingOutput_HasNoViolations()
    {
        var actual = new Dictionary<string, object?> { ["id"] = 5, ["name"] = "abc" };
        Assert.Empty(ManifestConformance.CheckOutput(Output, actual));
    }

    [Fact]
    public void MissingRequiredField_IsReported()
    {
        var actual = new Dictionary<string, object?> { ["id"] = 5 };
        var violation = Assert.Single(ManifestConformance.CheckOutput(Output, actual));
        Assert.Equal("name", violation.FieldName);
        Assert.Equal(ManifestConformance.ViolationKind.MissingRequiredField, violation.Kind);
    }

    [Fact]
    public void WrongFieldType_IsReported()
    {
        // "id" declared number but the task produced an object.
        var actual = new Dictionary<string, object?> { ["id"] = new Dictionary<string, object>(), ["name"] = "abc" };
        var violation = Assert.Single(ManifestConformance.CheckOutput(Output, actual));
        Assert.Equal("id", violation.FieldName);
        Assert.Equal(ManifestConformance.ViolationKind.FieldTypeMismatch, violation.Kind);
        Assert.Equal("object", violation.ActualType);
    }

    [Fact]
    public void UnstructuredOutput_IsNotChecked()
    {
        var unstructured = new OutputDefinition("result"); // no Fields
        Assert.Empty(ManifestConformance.CheckOutput(unstructured, new Dictionary<string, object?>()));
    }
}
