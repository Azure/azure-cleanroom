// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace CcfProvider;

internal class AzureFilesNodeStorageProvider : INodeStorageProvider
{
    private readonly string networkName;
    private readonly JsonObject providerConfig;
    private readonly InfraType infraType;
    private readonly ILogger logger;

    public AzureFilesNodeStorageProvider(
        string networkName,
        JsonObject? providerConfig,
        InfraType infraType,
        ILogger logger)
    {
        if (providerConfig == null)
        {
            throw new ArgumentNullException("proivderInput cannot be null for AzureFiles");
        }

        this.networkName = networkName;
        this.providerConfig = providerConfig;
        this.infraType = infraType;
        this.logger = logger;
    }

    public NodeStorageType GetNodeStorageType()
    {
        return NodeStorageType.AzureFiles;
    }

    public async Task<bool> NodeStorageDirectoryExists(string nodeName)
    {
        return await AzFileShare.FileShareExists(
            ShareName(nodeName),
            this.providerConfig);
    }

    public async Task CreateNodeStorageDirectory(string nodeName)
    {
        await AzFileShare.CreateFileShare(
            ShareName(nodeName),
            nodeName,
            this.networkName,
            this.providerConfig,
            this.logger.ProgressReporter());
    }

    public async Task DeleteNodeStorageDirectory(string nodeName)
    {
        var shareName = ShareName(nodeName);
        this.logger.LogWarning($"Deleting file share {shareName}.");
        await AzFileShare.DeleteFileShare(
            shareName,
            this.networkName,
            this.providerConfig);
    }

    public async Task DeleteUncommittedLedgerFiles(string nodeName)
    {
        await AzFileShare.DeleteUncommittedLedgerFiles(
            this.networkName,
            ShareName(nodeName),
            this.providerConfig,
            this.logger.ProgressReporter());
    }

    public Task<(IDirectory rwLedgerDir, IDirectory rwSnapshotsDir, IDirectory? logsDir)>
    GetReadWriteLedgerSnapshotsDir(string nodeName)
    {
        var shareName = ShareName(nodeName);
        IDirectory rwLedgerDir = new AzFileShareDirectory
        {
            MountPath = "/mnt/storage/ledger",
            VolumeMountPath = "/mnt/storage",
            ShareName = shareName
        };

        IDirectory rwSnapshotsDir = new AzFileShareDirectory
        {
            MountPath = "/mnt/storage/snapshots",
            VolumeMountPath = "/mnt/storage",
            ShareName = shareName
        };

        IDirectory logsDir = new AzFileShareDirectory
        {
            MountPath = "/mnt/storage/logs",
            VolumeMountPath = "/mnt/storage",
            ShareName = shareName
        };

        return Task.FromResult((rwLedgerDir, rwSnapshotsDir, (IDirectory?)logsDir));
    }

    public Task<(List<IDirectory> roLedgerDirs, IDirectory? roSnapshotsDir)>
        GetReadonlyLedgerSnapshotsDir()
    {
        return this.GetReadonlyLedgerSnapshotsDirForNetwork(this.networkName);
    }

    public async Task<(List<IDirectory> roLedgerDirs, IDirectory? roSnapshotsDir)>
        GetReadonlyLedgerSnapshotsDirForNetwork(string targetNetworkName)
    {
        IDirectory? roSnapshotsDir = null;
        string? latestSnapshotShareName;
        latestSnapshotShareName = await AzFileShare.FindShareWithLatestSnapshot(
            targetNetworkName,
            this.providerConfig,
            this.logger.ProgressReporter());
        if (latestSnapshotShareName != null)
        {
            roSnapshotsDir = new AzFileShareDirectory
            {
                ShareName = latestSnapshotShareName,
                MountPath = "/mnt/ro-snapshots/snapshots",
                VolumeMountPath = "/mnt/ro-snapshots"
            };
        }

        List<IDirectory> roLedgerDirs = new();
        List<(string nodeName, string shareName)> ledgerShares =
            await AzFileShare.FindSharesWithLedgers(
                targetNetworkName,
                committedLedgerFilesOnly: true,
                this.providerConfig,
                this.logger.ProgressReporter());
        int index = 0;
        foreach ((string _, string shareName) in ledgerShares)
        {
            roLedgerDirs.Add(new AzFileShareDirectory
            {
                ShareName = shareName,
                MountPath = $"/mnt/ro-ledgers-{index}/ledger",
                VolumeMountPath = $"/mnt/ro-ledgers-{index}"
            });
        }

        return (roLedgerDirs, roSnapshotsDir);
    }

    public async Task<List<string>> GetNodesWithLedgers()
    {
        List<(string nodeName, string shareName)> ledgerShares =
            await AzFileShare.FindSharesWithLedgers(
                this.networkName,
                committedLedgerFilesOnly: false,
                this.providerConfig,
                this.logger.ProgressReporter());
        return ledgerShares.ConvertAll(s => s.nodeName);
    }

    public async Task CopyLatestSnapshot(IDirectory? roSnapshotsDir, IDirectory rwSnapshotsDir)
    {
        if (roSnapshotsDir == null)
        {
            return;
        }

        var roSnapDir = (AzFileShareDirectory)roSnapshotsDir;
        var rwSnapDir = (AzFileShareDirectory)rwSnapshotsDir;

        if (roSnapDir.ShareName == rwSnapDir.ShareName)
        {
            return;
        }

        // Eg "snapshots/snapshot_13_14.committed".
        string? latestSnapshotName = await AzFileShare.FindLatestSnapshot(
            roSnapDir.ShareName,
            this.providerConfig,
            this.logger.ProgressReporter());
        if (!string.IsNullOrEmpty(latestSnapshotName))
        {
            if (!await AzFileShare.DirectoryExists(
                rwSnapDir.ShareName,
                "snapshots",
                this.providerConfig))
            {
                this.logger.LogInformation($"Creating destination directory " +
                    $"{rwSnapDir.ShareName}/snapshots for copying snapshot.");
                await AzFileShare.CreateDirectory(
                rwSnapDir.ShareName,
                "snapshots",
                this.providerConfig);
            }

            this.logger.LogInformation($"Copying {latestSnapshotName} to " +
                $"{rwSnapDir.ShareName}/snapshots.");

            Stopwatch timeTaken = Stopwatch.StartNew();
            await AzFileShare.Copy(
                roSnapDir.ShareName,
                latestSnapshotName,
                rwSnapDir.ShareName,
                this.providerConfig,
                this.logger.ProgressReporter());
            timeTaken.Stop();

            this.logger.LogInformation($"Copy finished in {timeTaken.ElapsedMilliseconds}ms: " +
                $"{latestSnapshotName} to {rwSnapDir.ShareName}/snapshots.");
        }
    }

    public Task AddNodeStorageProviderConfiguration(
        string nodeConfigDataDir,
        IDirectory rwLedgerDir,
        IDirectory rwSnapshotsDir,
        List<IDirectory>? roLedgerDirs,
        IDirectory? roSnapshotsDir)
    {
        return Task.CompletedTask;
    }

    public Task UpdateCreateContainerParams(
        string nodeName,
        List<IDirectory>? roLedgerDirs,
        IDirectory? roSnapshotsDir,
        CreateContainerParameters createContainerParams)
    {
        throw new NotImplementedException(
            "UpdateCreateContainerParams not supported for AzureFiles.");
    }

    public async Task UpdateCreateContainerGroupParams(
        IDirectory rwLedgerDir,
        IDirectory rwSnapshotsDir,
        List<IDirectory>? roLedgerDirs,
        IDirectory? roSnapshotsDir,
        ContainerGroupData createContainerParams)
    {
        var rwLedgerShare = (AzFileShareDirectory)rwLedgerDir;
        var rwSnapshotsShare = (AzFileShareDirectory)rwSnapshotsDir;

        if (rwLedgerShare.VolumeMountPath != rwSnapshotsShare.VolumeMountPath ||
            rwLedgerShare.ShareName != rwSnapshotsShare.ShareName)
        {
            throw new NotSupportedException(
                "Both rw ledger and snapshots dir should point to the same " +
                "volume mount path and backing file share. " +
                $"rwLedgerShare: {JsonSerializer.Serialize(rwLedgerShare)}, " +
                $"rwSnapshotsShare: {JsonSerializer.Serialize(rwSnapshotsShare)}.");
        }

        var cchost = createContainerParams.Containers.Single(
            c => c.Name == AciConstants.ContainerName.CcHost);

        string accountName = AzStorage.GetStorageAccountName(
            this.providerConfig.AzureFilesStorageAccountId());
        string accountKey =
            await AzStorage.GetStorageAccountKey(
                this.providerConfig.AzureFilesStorageAccountId(),
                this.providerConfig);

        string rwShareName = rwLedgerShare.ShareName;
        string rwVolumeMountPath = rwLedgerShare.VolumeMountPath;
        string rwVolumeName = "storagevolume";
        createContainerParams.Volumes.Add(new ContainerVolume(rwVolumeName)
        {
            AzureFile = new ContainerInstanceAzureFileVolume(rwShareName, accountName)
            {
                StorageAccountKey = accountKey
            }
        });
        cchost.VolumeMounts.Add(new ContainerVolumeMount(rwVolumeName, rwVolumeMountPath));

        if (roLedgerDirs != null)
        {
            int index = 0;
            foreach (var roLedgerDir in roLedgerDirs)
            {
                var dir = (AzFileShareDirectory)roLedgerDir;
                string roLedegerShareName = dir.ShareName;
                string volumeName = $"roledgervolume-{index}";
                createContainerParams.Volumes.Add(new ContainerVolume(volumeName)
                {
                    AzureFile = new ContainerInstanceAzureFileVolume(roLedegerShareName, accountName)
                    {
                        StorageAccountKey = accountKey,

                        // TODO (gsinha): Getting "invalid mount list" error during container group
                        // provisioning if this constraint is enforced by rego policy.
                        //IsReadOnly = true
                    }
                });
                cchost.VolumeMounts.Add(new ContainerVolumeMount(volumeName, dir.VolumeMountPath));
                index++;
            }
        }

        if (roSnapshotsDir != null)
        {
            var dir = (AzFileShareDirectory)roSnapshotsDir;
            string roSnapshotsShareName = dir.ShareName;
            string volumeName = "rosnapshotsvolume";
            createContainerParams.Volumes.Add(new ContainerVolume(volumeName)
            {
                AzureFile = new ContainerInstanceAzureFileVolume(roSnapshotsShareName, accountName)
                {
                    StorageAccountKey = accountKey,

                    // TODO (gsinha): Getting "invalid mount list" error during container group
                    // provisioning if this constraint is enforced by rego policy.
                    //IsReadOnly = true
                }
            });
            cchost.VolumeMounts.Add(new ContainerVolumeMount(volumeName, dir.VolumeMountPath));
        }
    }

    private static string ShareName(string nodeName)
    {
        return nodeName.ToLower();
    }
}

internal class AzFileShareDirectory : IDirectory
{
    public string ShareName { get; set; } = default!;

    public string MountPath { get; set; } = default!;

    public string VolumeMountPath { get; set; } = default!;
}
