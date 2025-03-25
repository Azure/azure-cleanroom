// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;

namespace CcfProvider;

public static class AzFileShare
{
    private const string CcfNetworkNameKey = "ccf_network_name";
    private const string CcfNodeNameKey = "ccf_node_name";

    public static async Task<string> CreateFileShare(
        string shareName,
        string nodeName,
        string networkName,
        JsonObject providerConfig,
        IProgress<string> progress)
    {
        var shareClient = await GetShareClient(shareName, providerConfig);
        TimeSpan retryTimeout = TimeSpan.FromSeconds(60);
        var stopwatch = Stopwatch.StartNew();
        int attempt = 1;
        while (true)
        {
            try
            {
                await shareClient.CreateIfNotExistsAsync(metadata: new Dictionary<string, string>
                {
                    {
                        CcfNetworkNameKey, networkName
                    },
                    {
                        CcfNodeNameKey, nodeName
                    }
                });
                return shareName;
            }
            catch (RequestFailedException rfe) when (rfe.ErrorCode == "ShareBeingDeleted")
            {
                if (stopwatch.Elapsed > retryTimeout)
                {
                    throw new TimeoutException(
                        $"Hit timeout waiting for '{shareName}' file share deletion.");
                }

                progress.Report(
                    $"Waiting for file share {shareName} to finish a pending " +
                    $"deletion. Attempt: {attempt}.");
                _ = attempt++;
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    public static async Task DeleteFileShare(
        string shareName,
        string networkName,
        JsonObject providerConfig)
    {
        ShareClient shareClient = await GetShareClient(shareName, providerConfig);
        await shareClient.DeleteIfExistsAsync();
    }

    public static async Task DeleteFileShares(
        string networkName,
        JsonObject providerConfig,
        IProgress<string> progress)
    {
        FileServiceResource fileService = await GetFileService(providerConfig);
        FileShareCollection fileShareCollection = fileService.GetFileShares();
        AsyncPageable<FileShareResource> response = fileShareCollection.GetAllAsync();

        List<FileShareResource> toDelete = new();
        await foreach (FileShareResource fileShare in response)
        {
            try
            {
                FileShareResource fsData = await fileShare.GetAsync();
                if (!fsData.Data.Metadata.TryGetValue(CcfNetworkNameKey, out var value) ||
                    value != networkName)
                {
                    // Skip containers that are not tagged with the network name.
                    continue;
                }

                progress.Report($"Going to delete file share '{fileShare.Data.Name}'.");
                toDelete.Add(fileShare);
            }
            catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                continue;
            }
        }

        List<Task> deleteTasks = new();
        foreach (var container in toDelete)
        {
            deleteTasks.Add(container.DeleteAsync(WaitUntil.Completed));
        }

        await Task.WhenAll(deleteTasks);
    }

    public static async Task<bool> FileShareExists(
        string shareName,
        JsonObject providerConfig)
    {
        FileServiceResource fileService = await GetFileService(providerConfig);
        FileShareCollection fileShareCollection = fileService.GetFileShares();
        bool exists = await fileShareCollection.ExistsAsync(shareName);
        return exists;
    }

    public static async Task<bool> DirectoryExists(
        string shareName,
        string directoryName,
        JsonObject providerConfig)
    {
        var shareClient = await GetShareClient(shareName, providerConfig);
        var directoryClient = shareClient.GetDirectoryClient(directoryName);
        return await directoryClient.ExistsAsync();
    }

    public static async Task CreateDirectory(
        string shareName,
        string directoryName,
        JsonObject providerConfig)
    {
        var shareClient = await GetShareClient(shareName, providerConfig);
        await shareClient.CreateDirectoryAsync(directoryName);
    }

    public static async Task Copy(
        string sourceShareName,
        string sourceFilePath,
        string destinationShareName,
        JsonObject providerConfig,
        IProgress<string> progress)
    {
        var srcFileClient =
            await GetShareFileClient(sourceShareName, sourceFilePath, providerConfig);
        string dstFilePath = sourceFilePath;
        var dstFileClient =
            await GetShareFileClient(destinationShareName, dstFilePath, providerConfig);

        // https://learn.microsoft.com/en-us/azure/storage/files/storage-dotnet-how-to-use-files#copy-a-file-to-another-file
        await dstFileClient.StartCopyAsync(srcFileClient.Uri);
        while (true)
        {
            ShareFileProperties properties = await dstFileClient.GetPropertiesAsync();
            if (properties.CopyStatus == CopyStatus.Success)
            {
                progress.Report($"File copy operation successfully completed from " +
                    $"{srcFileClient.Uri} to " +
                    $"{dstFileClient.Uri}: " +
                    $"{JsonSerializer.Serialize(properties, Utils.Options)}.");
                break;
            }

            if (properties.CopyStatus == CopyStatus.Aborted ||
                properties.CopyStatus == CopyStatus.Failed)
            {
                throw new Exception(
                    $"File copy operation failed from {srcFileClient.Uri} to {dstFileClient.Uri} " +
                    $": {JsonSerializer.Serialize(properties, Utils.Options)}.");
            }

            if (properties.CopyStatus == CopyStatus.Pending)
            {
                progress.Report($"File copy operation is pending from {srcFileClient.Uri} to " +
                    $"{dstFileClient.Uri}: " +
                    $"{JsonSerializer.Serialize(properties, Utils.Options)}.");
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            throw new NotSupportedException(
                $"Unexpected copyStatus {properties.CopyStatus}. Fix this.");
        }
    }

    public static async Task<string?> FindShareWithLatestSnapshot(
        string networkName,
        JsonObject providerConfig,
        IProgress<string> progress)
    {
        FileServiceResource fileService = await GetFileService(providerConfig);
        FileShareCollection fileShareCollection = fileService.GetFileShares();

        AsyncPageable<FileShareResource> response = fileShareCollection.GetAllAsync();

        string? latestShareName = null;
        string? latestSnapshot = null;
        int latestSnapshotSeqNo = 0;
        int numberOfShares = 0;
        await foreach (FileShareResource fsItem in response)
        {
            numberOfShares++;

            // Get or else Metadata is not populated.
            FileShareResource fileShare = await fsItem.GetAsync();
            if (!fileShare.Data.Metadata.TryGetValue(CcfNetworkNameKey, out var value) ||
                value != networkName)
            {
                continue;
            }

            progress.Report(
                $"Inspecting file share '{fileShare.Data.Name}' for snapshots.");
            var share = await GetShareClient(fileShare.Data.Name, providerConfig);
            var dir = share.GetDirectoryClient("snapshots");
            List<string> snapshotFiles = new();
            if (await dir.ExistsAsync())
            {
                await foreach (ShareFileItem item in dir.GetFilesAndDirectoriesAsync())
                {
                    progress.Report($"Found file {item.Name} on file share " +
                        $"'{fileShare.Data.Name}'.");

                    // fileName = snapshot_13_14.committed.
                    var fileName = item.Name;
                    if (fileName.StartsWith("snapshot_") && fileName.EndsWith(".committed"))
                    {
                        snapshotFiles.Add(fileName);
                    }
                }
            }

            string shareName = fileShare.Data.Name;
            var latestNodeSnapshot =
                snapshotFiles.OrderBy(f => f.PadForNaturalNumberOrdering()).LastOrDefault();
            if (latestNodeSnapshot != null)
            {
                progress.Report(
                    $"Latest snapshot on share '{shareName}' is: {latestNodeSnapshot}.");
                int latestNodeSnapshostSeqNo = int.Parse(latestNodeSnapshot.Split("_")[1]);
                if (latestNodeSnapshostSeqNo > latestSnapshotSeqNo)
                {
                    progress.Report(
                        $"Snapshot '{latestNodeSnapshot}' on share '{shareName}' is the " +
                        $"latest seen till now. Previous latest was " +
                        $"'{latestSnapshot}' on share '{latestShareName}'.");
                    latestSnapshot = latestNodeSnapshot;
                    latestSnapshotSeqNo = latestNodeSnapshostSeqNo;
                    latestShareName = shareName;
                }
            }
            else
            {
                progress.Report($"Did not find any snapshot on share '{shareName}'.");
            }
        }

        if (latestShareName != null)
        {
            progress.Report(
                $"Located latest snapshot with seq no {latestSnapshotSeqNo} on share " +
                $"'{latestShareName}': '{latestSnapshot}'.");
        }
        else
        {
            progress.Report(
                $"Did not locate any latest snapshot for network {networkName}. " +
                $"Number of shares inspected: {numberOfShares}.");
        }

        return latestShareName;
    }

    public static async Task<string?> FindLatestSnapshot(
        string shareName,
        JsonObject providerConfig,
        IProgress<string> progress)
    {
        FileServiceResource fileService = await GetFileService(providerConfig);
        FileShareCollection fileShareCollection = fileService.GetFileShares();

        AsyncPageable<FileShareResource> response = fileShareCollection.GetAllAsync();

        progress.Report(
            $"Locating latest snapshot on file share '{shareName}'.");
        var share = await GetShareClient(shareName, providerConfig);
        var dir = share.GetDirectoryClient("snapshots");
        List<string> snapshotFiles = new();
        if (await dir.ExistsAsync())
        {
            await foreach (ShareFileItem item in dir.GetFilesAndDirectoriesAsync())
            {
                progress.Report($"Found file {item.Name} on file share '{shareName}'.");

                // fileName = snapshot_13_14.committed.
                var fileName = item.Name;
                if (fileName.StartsWith("snapshot_") && fileName.EndsWith(".committed"))
                {
                    snapshotFiles.Add(fileName);
                }
            }
        }

        var latestSnapshot =
            snapshotFiles.OrderBy(f => f.PadForNaturalNumberOrdering()).LastOrDefault();
        if (latestSnapshot != null)
        {
            progress.Report(
                $"Latest snapshot on share '{shareName}' is: {latestSnapshot}.");
        }
        else
        {
            progress.Report($"Did not find any snapshot on share '{shareName}'.");
        }

        return "snapshots/" + latestSnapshot;
    }

    public static async Task DeleteUncommittedLedgerFiles(
        string networkName,
        string shareName,
        JsonObject providerConfig,
        IProgress<string> progress)
    {
        FileServiceResource fileService = await GetFileService(providerConfig);
        FileShareCollection fileShareCollection = fileService.GetFileShares();

        AsyncPageable<FileShareResource> response = fileShareCollection.GetAllAsync();

        bool filesDeleted = false;
        await foreach (FileShareResource fsItem in response)
        {
            // Get or else Metadata is not populated.
            FileShareResource fileShare = await fsItem.GetAsync();
            if (!fileShare.Data.Metadata.TryGetValue(CcfNetworkNameKey, out var value) ||
                value != networkName)
            {
                continue;
            }

            if (fileShare.Data.Name != shareName)
            {
                continue;
            }

            progress.Report(
                $"Inspecting file share '{fileShare.Data.Name}' for uncommitted ledger files.");
            var share = await GetShareClient(fileShare.Data.Name, providerConfig);
            var dir = share.GetDirectoryClient("ledger");
            if (await dir.ExistsAsync())
            {
                await foreach (ShareFileItem item in dir.GetFilesAndDirectoriesAsync())
                {
                    progress.Report($"Found file {item.Name} on file share " +
                        $"'{fileShare.Data.Name}'.");

                    // fileName = ledger_13_14 or ledger_12.
                    // Avoid .committed files so avoid any file that has a . in its name.
                    var fileName = item.Name;
                    if (fileName.StartsWith("ledger_") && !fileName.Contains('.'))
                    {
                        // Delete the file.
                        progress.Report($"Removing file {item.Name} from file share " +
                            $"'{fileShare.Data.Name}'.");
                        await DeleteFileWithRetries(dir, item.Name);
                        filesDeleted = true;
                    }
                }
            }
        }

        if (!filesDeleted)
        {
            progress.Report($"Did not find any uncommitted ledger files to delete on file share " +
                $"'{shareName}'.");
        }

        async Task DeleteFileWithRetries(ShareDirectoryClient dir, string fileName)
        {
            TimeSpan retryTimeout = TimeSpan.FromSeconds(90);
            var stopwatch = Stopwatch.StartNew();
            int attempt = 1;
            while (true)
            {
                try
                {
                    await dir.DeleteFileAsync(fileName);
                    return;
                }
                catch (RequestFailedException rfe) when (rfe.ErrorCode == "SharingViolation")
                {
                    if (stopwatch.Elapsed > retryTimeout)
                    {
                        throw;
                    }

                    progress.Report(
                        $"Waiting for file {fileName} to be not in use (SharingViolation) " +
                        $"before again deleting it. Attempt: {attempt}.");
                    attempt++;
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
    }

    public static async Task<List<(string nodeName, string shareName)>>
        FindSharesWithLedgers(
        string networkName,
        bool committedLedgerFilesOnly,
        JsonObject providerConfig,
        IProgress<string> progress)
    {
        FileServiceResource fileService = await GetFileService(providerConfig);
        FileShareCollection fileShareCollection = fileService.GetFileShares();

        AsyncPageable<FileShareResource> response = fileShareCollection.GetAllAsync();

        int numberOfShares = 0;
        List<FileShareResource> sharesWithLedgers = new();
        await foreach (FileShareResource fsItem in response)
        {
            numberOfShares++;

            // Get or else Metadata is not populated.
            FileShareResource fileShare = await fsItem.GetAsync();
            if (!fileShare.Data.Metadata.TryGetValue(CcfNetworkNameKey, out var value) ||
                value != networkName)
            {
                continue;
            }

            progress.Report(
                $"Inspecting file share '{fileShare.Data.Name}' for ledgers. " +
                $"committedLedgerFilesOnly: {committedLedgerFilesOnly}");
            var share = await GetShareClient(fileShare.Data.Name, providerConfig);
            var dir = share.GetDirectoryClient("ledger");
            if (await dir.ExistsAsync())
            {
                await foreach (ShareFileItem item in dir.GetFilesAndDirectoriesAsync())
                {
                    progress.Report($"Found file {item.Name} on file share " +
                        $"'{fileShare.Data.Name}'.");

                    // fileName = ledger_13_14.committed or ledger_12.
                    var fileName = item.Name;
                    if (fileName.StartsWith("ledger_"))
                    {
                        if (committedLedgerFilesOnly)
                        {
                            if (fileName.EndsWith(".committed"))
                            {
                                sharesWithLedgers.Add(fileShare);
                            }
                        }
                        else
                        {
                            sharesWithLedgers.Add(fileShare);
                        }
                    }
                }
            }
        }

        sharesWithLedgers = sharesWithLedgers.DistinctBy(s => s.Data.Name).ToList();
        if (sharesWithLedgers.Any())
        {
            progress.Report(
                $"Located ledger files (committedLedgerFilesOnly: {committedLedgerFilesOnly}) " +
                $"on shares(s): " +
                $"{JsonSerializer.Serialize(sharesWithLedgers.Select(s => s.Data.Name))}.");
        }
        else
        {
            progress.Report(
                $"Did not locate any ledger files " +
                $"(committedLedgerFilesOnly: {committedLedgerFilesOnly}) for network " +
                $"{networkName}. " +
                $"Number of shares inspected: {numberOfShares}.");
        }

        List<(string nodeName, string shareName)> result = new();
        return sharesWithLedgers.ConvertAll(fileShare =>
        {
            if (!fileShare.Data.Metadata.TryGetValue(CcfNodeNameKey, out var nodeName))
            {
                nodeName = fileShare.Data.Name;
            }

            return (nodeName, fileShare.Data.Name);
        });
    }

    private static async Task<FileServiceResource> GetFileService(JsonObject providerConfig)
    {
        var accountId = new ResourceIdentifier(providerConfig.AzureFilesStorageAccountId());
        string subscriptionId = accountId.SubscriptionId!;
        string resourceGroupName = accountId.ResourceGroupName!;
        string accountName = AzStorage.GetStorageAccountName(
            providerConfig.AzureFilesStorageAccountId());

        var armClient = new ArmClient(new DefaultAzureCredential());
        var resourceIdentifier = new ResourceIdentifier($"/subscriptions/{subscriptionId}");
        SubscriptionResource subscription = armClient.GetSubscriptionResource(resourceIdentifier);

        ResourceGroupResource resourceGroup =
            await subscription.GetResourceGroupAsync(resourceGroupName);
        StorageAccountResource storageAccount =
            await resourceGroup.GetStorageAccountAsync(accountName);
        FileServiceResource fileService = storageAccount.GetFileService();
        return fileService;
    }

    private static async Task<ShareClient> GetShareClient(
        string shareName,
        JsonObject providerConfig)
    {
        var connectionString = await GetConnectionString(providerConfig);
        var shareClient = new ShareClient(connectionString, shareName);
        return shareClient;
    }

    private static async Task<ShareFileClient> GetShareFileClient(
        string shareName,
        string filePath,
        JsonObject providerConfig)
    {
        var connectionString = await GetConnectionString(providerConfig);
        var shareFileClient = new ShareFileClient(connectionString, shareName, filePath);
        return shareFileClient;
    }

    private static async Task<string> GetConnectionString(JsonObject providerConfig)
    {
        string accountName = AzStorage.GetStorageAccountName(
            providerConfig.AzureFilesStorageAccountId());
        string accountKey = await AzStorage.GetStorageAccountKey(
            providerConfig.AzureFilesStorageAccountId());

        string connectionString =
            $"DefaultEndpointsProtocol=https;AccountName={accountName};" +
            $"AccountKey={accountKey};" +
            $"EndpointSuffix=core.windows.net";
        return connectionString;
    }
}
