// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Logging;

namespace CcfProvider;

public static class Utils
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static async Task<string> PackDirectory(string scratchDir)
    {
        string tgzFile = scratchDir + ".tgz";
        using (var ms = new MemoryStream())
        {
            await TarFile.CreateFromDirectoryAsync(
                scratchDir,
                ms,
                includeBaseDirectory: false);

            ms.Position = 0;
            File.Delete(tgzFile);
            using FileStream compressedFs = File.Create(tgzFile);
            using var compressor = new GZipStream(compressedFs, CompressionMode.Compress);
            await ms.CopyToAsync(compressor);
        }

        byte[] tgzFileBytes = await File.ReadAllBytesAsync(tgzFile);
        if (tgzFileBytes.Length < 1)
        {
            // Something went wrong with tgz creation logic above.
            throw new Exception("Unexpected 0 byte size for tgz. Expected a larger size.");
        }

        string tgzFileBase64String = Convert.ToBase64String(tgzFileBytes);
        return tgzFileBase64String;
    }

    public static string GetUniqueString(string id, int length = 13)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(id);
        var hashedInputBytes = SHA512.HashData(bytes);
        List<char> a = new();
        for (int i = 1; i <= length; i++)
        {
            var b = hashedInputBytes[i];
            var x = (char)((b % 26) + (byte)'a');
            a.Add(x);
        }

        return new string(a.ToArray());
    }

    public static string PadForNaturalNumberOrdering(this string input)
    {
        // https://stackoverflow.com/questions/12077182/c-sharp-sort-files-by-natural-number-ordering-in-the-name
        return Regex.Replace(
            input,
            @"\d+",
            match => match.Value.PadLeft(9, '0'));
    }

    public static NodeStorageType GetNodeStorageType(
        this JsonObject? providerConfig,
        InfraType infraType)
    {
        if (providerConfig != null &&
            providerConfig.ContainsKey("azureFiles") &&
            providerConfig["azureFiles"] != null)
        {
            return NodeStorageType.AzureFiles;
        }

        return infraType == InfraType.@virtual ?
            NodeStorageType.DockerHostFs : NodeStorageType.LocalFs;
    }

    public static bool FastJoin(this JsonObject? providerConfig, NodeStorageType nodeStorageType)
    {
        if (providerConfig != null &&
            providerConfig.ContainsKey("fastJoin") &&
            !string.IsNullOrEmpty(providerConfig["fastJoin"]?.ToString()))
        {
            return providerConfig["fastJoin"]!.ToString() == "true";
        }

        return true;
    }

    public static string AzureFilesStorageAccountId(this JsonObject providerConfig)
    {
        if (providerConfig.ContainsKey("azureFiles"))
        {
            return providerConfig["azureFiles"]!["storageAccountId"]!.ToString();
        }

        throw new ArgumentNullException("No storageAccountId input in providerConfig.");
    }

    public static bool StartNodeSleep(this JsonObject? providerConfig)
    {
        if (providerConfig != null &&
            providerConfig.ContainsKey("startNodeSleep") &&
            !string.IsNullOrEmpty(providerConfig["startNodeSleep"]?.ToString()))
        {
            return providerConfig["startNodeSleep"]!.ToString() == "true";
        }

        return false;
    }

    public static bool JoinNodeSleep(this JsonObject? providerConfig)
    {
        if (providerConfig != null &&
            providerConfig.ContainsKey("joinNodeSleep") &&
            !string.IsNullOrEmpty(providerConfig["joinNodeSleep"]?.ToString()))
        {
            return providerConfig["joinNodeSleep"]!.ToString() == "true";
        }

        return false;
    }

    public static IProgress<string> ProgressReporter(this ILogger logger)
    {
        return new Progress<string>(m => logger.LogInformation(m));
    }

    public static List<string> NodeSanFormat(this string fqdn)
    {
        return new List<string> { $"dNSName:{fqdn}" };
    }

    public static RSA ToRSAKey(string encryptionPrivateKey)
    {
        var rsaEncKey = RSA.Create();
        rsaEncKey.ImportFromPem(encryptionPrivateKey);
        return rsaEncKey;
    }

    public static async Task<RSA> ToRSAKey(Uri encryptionKeyId)
    {
        var creds = new DefaultAzureCredential();
        var cryptographyClient = new CryptographyClient(encryptionKeyId, creds);
        var rsaEncKey = await cryptographyClient.CreateRSAAsync();
        return rsaEncKey;
    }
}
