// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Security;

/// <summary>One privileged node type found in an imported workflow, with the sensitive capabilities it carries.</summary>
public sealed record PrivilegedNodeInfo(string NodeType, string DisplayName, IReadOnlyList<string> Capabilities);

/// <summary>
/// Scans a set of nodes for privileged capabilities (filesystem / code execution / database) by resolving
/// each node type's manifest. Used to warn before installing an imported template or bundle — a graph
/// carrying these can touch the host beyond ordinary data flow. Deduplicated by node type.
/// </summary>
public static class PrivilegedNodeScanner
{
    public static async Task<IReadOnlyList<PrivilegedNodeInfo>> ScanAsync(
        INodePackageManifestProvider manifests,
        IEnumerable<NodeDefinition> nodes,
        CancellationToken cancellationToken = default)
    {
        var result = new List<PrivilegedNodeInfo>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Type) || !seen.Add(node.Type))
            {
                continue;
            }

            var manifest = await manifests.GetManifestAsync(new NodePackageId(node.Type), cancellationToken);
            if (manifest is null)
            {
                continue;
            }

            var privileged = manifest.Capabilities.Where(NodeCapabilities.IsPrivileged).ToList();
            if (privileged.Count > 0)
            {
                result.Add(new PrivilegedNodeInfo(node.Type, manifest.DisplayName, privileged));
            }
        }

        return result;
    }
}
