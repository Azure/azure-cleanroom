// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfCommon;

public interface IMemberStore
{
    Task<SigningKeyInfo> GenerateSigningKey(string memberName);

    Task<SigningKeyInfo?> GetSigningKey(string memberName);

    Task<SigningPrivateKeyInfo> ReleaseSigningKey(string memberName);

    Task<EncryptionKeyInfo> GenerateEncryptionKey(string memberName);

    Task<EncryptionKeyInfo?> GetEncryptionKey(string memberName);

    Task<EncryptionPrivateKeyInfo> ReleaseEncryptionKey(string memberName);

    Task<List<string>> GetMembers();
}
