// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using TokenCredentials;

namespace CcfProvider;

internal static class AzStorage
{
    public static async Task<string> CreateStorageAccount(
        string accountName,
        string resourceGroupName,
        string subscriptionId,
        string location,
        JsonObject providerConfig)
    {
        var armClient = new ArmClient(TokenCredentialFactory.GetTenantCredential(providerConfig));
        var resourceIdentifier = new ResourceIdentifier($"/subscriptions/{subscriptionId}");
        SubscriptionResource subscription = armClient.GetSubscriptionResource(resourceIdentifier);

        ResourceGroupResource resourceGroup =
            await subscription.GetResourceGroupAsync(resourceGroupName);

        var sku = new StorageSku(StorageSkuName.StandardGrs);
        StorageKind kind = StorageKind.StorageV2;
        var parameters = new StorageAccountCreateOrUpdateContent(sku, kind, location);

        StorageAccountCollection accountCollection = resourceGroup.GetStorageAccounts();
        ArmOperation<StorageAccountResource> accountCreateOperation =
            await accountCollection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                accountName,
                parameters);

        StorageAccountResource storageAccount = accountCreateOperation.Value;
        return storageAccount.Id.ToString();
    }

    public static async Task<string> GetStorageAccountKey(
        string storageAccountId,
        JsonObject providerConfig)
    {
        var accountId = new ResourceIdentifier(storageAccountId);
        string subscriptionId = accountId.SubscriptionId!;
        string resourceGroupName = accountId.ResourceGroupName!;

        var armClient = new ArmClient(TokenCredentialFactory.GetTenantCredential(providerConfig));
        var resourceIdentifier = new ResourceIdentifier($"/subscriptions/{subscriptionId}");
        SubscriptionResource subscription = armClient.GetSubscriptionResource(resourceIdentifier);

        ResourceGroupResource resourceGroup =
            await subscription.GetResourceGroupAsync(resourceGroupName);
        string accountName = GetStorageAccountName(storageAccountId);

        StorageAccountResource storageAccount =
            await resourceGroup.GetStorageAccountAsync(accountName);

        storageAccount.GetKeysAsync();
        await foreach (StorageAccountKey key in storageAccount.GetKeysAsync())
        {
            return key.Value;
        }

        throw new Exception($"Not expecting to get 0 keys for storage account {accountName}.");
    }

    public static string GetStorageAccountName(string storageAccountId)
    {
        var accountId = new ResourceIdentifier(storageAccountId);
        string storageAccountName = accountId.Name;
        return storageAccountName;
    }
}
