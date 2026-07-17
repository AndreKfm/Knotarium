// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Knotarium.Core.Contracts;

public interface INodeTaskRegistry
{
    INodeTask? GetTask(string nodeType);
}
