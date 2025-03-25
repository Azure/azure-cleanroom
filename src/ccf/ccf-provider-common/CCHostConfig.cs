// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcfProvider;

public class CCHostConfig
{
    private readonly JsonObject cchostConfig;
    private readonly string outDir;

    private CCHostConfig(JsonObject cchostConfig, string outDir)
    {
        this.cchostConfig = cchostConfig;
        this.outDir = outDir;
    }

    public static async Task<CCHostConfig> InitConfig(
        string templatePath,
        string outDir)
    {
        var configTemplate = await File.ReadAllTextAsync(templatePath);
        var cchostConfig = JsonSerializer.Deserialize<JsonObject>(configTemplate)!;
        return new CCHostConfig(cchostConfig, outDir);
    }

    public void SetPublishedAddress(string fqdn)
    {
        var network = this.cchostConfig["network"]!;
        network["node_to_node_interface"]!["published_address"] =
            fqdn + $":{Ports.NodeToNodePort}";
        network["rpc_interfaces"]!["primary_rpc_interface"]!["published_address"] =
            fqdn + $":{Ports.RpcMainPort}";
        network["rpc_interfaces"]!["debug_interface"]!["published_address"] =
            fqdn + $":{Ports.RpcDebugPort}";
    }

    public void SetNodeLogLevel(string? nodeLogLevel)
    {
        if (!string.IsNullOrEmpty(nodeLogLevel))
        {
            this.cchostConfig["logging"] = new JsonObject
            {
                ["host_level"] = nodeLogLevel
            };
        }
    }

    public async Task SetNodeData(NodeData nodeData)
    {
        this.cchostConfig["node_data_json_file"] = $"/app/node_data.json";
        string nodeDataPath = this.outDir + $"/node_data.json";
        await File.WriteAllTextAsync(
            nodeDataPath,
            JsonSerializer.Serialize(nodeData, Utils.Options));
    }

    public void SetLedgerSnapshotsDirectory(
        string ledgerDirectory,
        string snapshotsDirectory,
        List<string>? roLedgerDirectories = null,
        string? roSnapshotsDirectory = null)
    {
        this.cchostConfig["ledger"] = new JsonObject
        {
            ["directory"] = ledgerDirectory
        };
        this.cchostConfig["snapshots"] = new JsonObject
        {
            ["directory"] = snapshotsDirectory
        };

        if (roLedgerDirectories?.Count > 0)
        {
            var entries = new JsonArray();
            roLedgerDirectories.ForEach(e => entries.Add(e));
            this.cchostConfig["ledger"]!["read_only_directories"] = entries;
        }

        if (!string.IsNullOrEmpty(roSnapshotsDirectory))
        {
            this.cchostConfig["snapshots"]!["read_only_directory"] = roSnapshotsDirectory;
        }
    }

    public void SetSubjectAltNames(List<string> san)
    {
        var sanArray = new JsonArray();
        san.ForEach(s => sanArray.Add(s));
        this.cchostConfig["node_certificate"]!["subject_alt_names"] = sanArray;
    }

    public async Task SetStartConfiguration(
        List<InitialMember> initialMembers,
        string constitutionFilesDir)
    {
        await this.SetMembers(initialMembers);
        this.SetConstitution(constitutionFilesDir);
    }

    public async Task SetJoinConfiguration(string targetRpcAddress, string serviceCertPem)
    {
        this.cchostConfig["command"]!["join"]!["target_rpc_address"] = targetRpcAddress;
        string serviceCertPath = this.outDir + "/service_cert.pem";
        await File.WriteAllTextAsync(serviceCertPath, serviceCertPem);
    }

    public async Task SetRecoverConfiguration(string previousServiceCertPem)
    {
        var serviceCertPath = this.outDir + "/previous_service_cert.pem";
        await File.WriteAllTextAsync(serviceCertPath, previousServiceCertPem);
    }

    public async Task<string> SaveConfig()
    {
        var ccConfigPath = this.outDir + "/cchost_config.json";
        await File.WriteAllTextAsync(
            ccConfigPath,
            JsonSerializer.Serialize(this.cchostConfig, Utils.Options));
        return ccConfigPath;
    }

    private async Task SetMembers(List<InitialMember> initialMembers)
    {
        string membersDir = this.outDir + "/members";
        Directory.CreateDirectory(membersDir);
        var membersArray = new JsonArray();
        for (int index = 0; index < initialMembers.Count; index++)
        {
            string memberCertPath = membersDir + $"/member{index}_cert.pem";
            string memberEncKeyPath = membersDir + $"/member{index}_enc_pubk.pem";
            string memberDataPath = membersDir + $"/member{index}_data.json";
            var memberData = initialMembers[index].MemberData ?? new JsonObject();

            await File.WriteAllTextAsync(
                memberCertPath,
                initialMembers[index].Certificate);
            await File.WriteAllTextAsync(
                memberDataPath,
                JsonSerializer.Serialize(memberData, Utils.Options));

            var entry = new JsonObject
            {
                ["certificate_file"] = $"/app/members/member{index}_cert.pem",
                ["data_json_file"] = $"/app/members/member{index}_data.json",
            };

            if (!string.IsNullOrEmpty(initialMembers[index].EncryptionPublicKey))
            {
                await File.WriteAllTextAsync(
                    memberEncKeyPath,
                    initialMembers[index].EncryptionPublicKey);
                entry["encryption_public_key_file"] = $"/app/members/member{index}_enc_pubk.pem";
            }

            membersArray.Add(entry);
        }

        this.cchostConfig["command"]!["start"]!["members"] = membersArray;
    }

    private void SetConstitution(string constitutionFilesDir)
    {
        // Copy over the default constitution so that we can start a CCF node with a default
        // constitution which would subsequently be overwritten via a proposal.
        string sandoxConstitutionDir = this.outDir + "/constitution/default";
        Directory.CreateDirectory(sandoxConstitutionDir);
        foreach (var filePath in Directory.GetFiles(
            constitutionFilesDir,
            "*.js",
            new EnumerationOptions
            {
                RecurseSubdirectories = true
            }))
        {
            string fileName = Path.GetFileName(filePath);
            File.Copy(filePath, sandoxConstitutionDir + $"/{fileName}", overwrite: true);
        }
    }
}
