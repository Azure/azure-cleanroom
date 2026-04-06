// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfCommon;

public interface IKeyStore
{
    Task<SigningKeyInfo> GenerateSigningKey(
        string kid,
        string kty,
        Dictionary<string, string> tags);

    Task<SigningKeyInfo?> GetSigningKey(string kid);

    Task<SigningPrivateKeyInfo> ReleaseSigningKey(string kid);

    Task<EncryptionKeyInfo> GenerateEncryptionKey(
        string kid,
        string kty,
        Dictionary<string, string> tags);

    Task<EncryptionKeyInfo?> GetEncryptionKey(string kid);

    Task<EncryptionPrivateKeyInfo> ReleaseEncryptionKey(string kid);

    Task<List<(string, IDictionary<string, string>)>> ListEncryptionKeys(string kty);
}
