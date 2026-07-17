// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Domain.OpenApi;

namespace Knotarium.Features.OpenApi;

public sealed record OperationGroup(string Tag, IReadOnlyList<ApiOperation> Operations);

public static class OpenApiGrouper
{
    /// <summary>
    /// Groups operations by primary tag. Untagged operations use the first path segment as group name.
    /// </summary>
    public static IReadOnlyList<OperationGroup> Group(IReadOnlyList<ApiOperation> operations)
    {
        return operations
            .GroupBy(op => op.Tags.Count > 0 ? op.Tags[0] : FirstPathSegment(op.PathTemplate))
            .OrderBy(g => g.Key)
            .Select(g => new OperationGroup(g.Key, g.ToList()))
            .ToList();
    }

    private static string FirstPathSegment(string pathTemplate)
    {
        var trimmed = pathTemplate.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }
}
