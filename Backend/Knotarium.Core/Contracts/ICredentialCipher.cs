// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Contracts;

public interface ICredentialCipher
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}