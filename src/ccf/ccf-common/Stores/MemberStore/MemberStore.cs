// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using AttestationClient;

namespace CcfCommon;

public class MemberStore : IMemberStore
{
    private const string MemberKeyNameFormat = "mk-{0}-{1}";
    private const string KeyTypeTagValue = "member-key";
    private const string MemberNameTagName = "member-name";

    private readonly IKeyStore keyStore;
    private readonly ConcurrentDictionary<string, SigningPrivateKeyInfo>
        cachedSigningKeys = new();

    public MemberStore(IKeyStore keyStore)
    {
        this.keyStore = keyStore;
    }

    public async Task<EncryptionKeyInfo> GenerateEncryptionKey(string memberName)
    {
        var memberKeyName = await ToMemberKeyName(memberName);
        return await this.keyStore.GenerateEncryptionKey(
            memberKeyName,
            KeyTypeTagValue,
            new()
            {
                {
                    MemberNameTagName, memberName
                }
            });
    }

    public async Task<SigningKeyInfo> GenerateSigningKey(string memberName)
    {
        var memberKeyName = await ToMemberKeyName(memberName);
        return await this.keyStore.GenerateSigningKey(
            memberKeyName,
            KeyTypeTagValue,
            new()
            {
                {
                    MemberNameTagName, memberName
                }
            });
    }

    public async Task<EncryptionKeyInfo?> GetEncryptionKey(string memberName)
    {
        var memberKeyName = await ToMemberKeyName(memberName);
        return await this.keyStore.GetEncryptionKey(memberKeyName);
    }

    public async Task<List<string>> GetMembers()
    {
        var names = new List<string>();
        var keys = await this.keyStore.ListEncryptionKeys(KeyTypeTagValue);
        if (keys.Any())
        {
            var hostData = await Attestation.GetCACIHostData();
            foreach (var item in keys)
            {
                var kid = item.Item1;
                if (kid.EndsWith(hostData) &&
                    item.Item2.TryGetValue(MemberNameTagName, out var name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    public async Task<SigningKeyInfo?> GetSigningKey(string memberName)
    {
        var memberKeyName = await ToMemberKeyName(memberName);
        return await this.keyStore.GetSigningKey(memberKeyName);
    }

    public async Task<EncryptionPrivateKeyInfo> ReleaseEncryptionKey(string memberName)
    {
        var memberKeyName = await ToMemberKeyName(memberName);
        return await this.keyStore.ReleaseEncryptionKey(memberKeyName);
    }

    public async Task<SigningPrivateKeyInfo> ReleaseSigningKey(string memberName)
    {
        if (this.cachedSigningKeys.TryGetValue(memberName, out var cached))
        {
            return cached;
        }

        var memberKeyName = await ToMemberKeyName(memberName);
        var signingKeyInfo = await this.keyStore.ReleaseSigningKey(memberKeyName);
        this.cachedSigningKeys.TryAdd(memberName, signingKeyInfo);
        return signingKeyInfo;
    }

    private static async Task<string> ToMemberKeyName(string memberName)
    {
        var hostData = await Attestation.GetCACIHostData();
        return string.Format(MemberKeyNameFormat, memberName, hostData);
    }

    private static string ExtractMemberName(string keyName)
    {
        return keyName.Split("-")[1];
    }
}
