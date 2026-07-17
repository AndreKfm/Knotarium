// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

public sealed record GenerationRequest(string Prompt, string Name);

public sealed record GeneratedPackage(string PackageId, string ManifestYaml, string ExecutorCode);

public interface INodePackageGenerator
{
    Task<GeneratedPackage> GenerateAsync(GenerationRequest request, CancellationToken ct);
}
