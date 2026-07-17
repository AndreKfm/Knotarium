// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Contracts;

public interface ICorrelationTokenCrypto
{
    string GenerateRawToken();
    string HashToken(string rawToken);
}
