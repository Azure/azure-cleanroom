// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Controllers;
using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

public class ClusterProvider
{
    private ILogger logger;
    private ICleanRoomClusterProvider clusterProvider;

    public ClusterProvider(
        ILogger logger,
        ICleanRoomClusterProvider clusterProvider)
    {
        this.logger = logger;
        this.clusterProvider = clusterProvider;
    }

    public ODataError? CreateClusterValidate(
    string clusterName,
    CleanRoomClusterInput? input,
    JsonObject? providerConfig)
    {
        return this.clusterProvider.CreateClusterValidate(
            clusterName,
            input,
            providerConfig);
    }

    public async Task<CleanRoomCluster> CreateCluster(
        string clusterName,
        CleanRoomClusterInput input,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        return await this.clusterProvider.CreateCluster(
            clusterName,
            input,
            providerConfig,
            progressReporter);
    }

    public async Task<CleanRoomCluster?> UpdateCluster(
        string clusterName,
        CleanRoomClusterInput input,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        return await this.clusterProvider.UpdateCluster(
            clusterName,
            input,
            providerConfig,
            progressReporter);
    }

    public ODataError? GetClusterValidate(
        string clusterName,
        JsonObject? providerConfig)
    {
        return this.clusterProvider.GetClusterValidate(
            clusterName,
            providerConfig);
    }

    public ODataError? GetClusterKubeConfigValidate(
        string clusterName,
        JsonObject? providerConfig)
    {
        return this.clusterProvider.GetClusterKubeConfigValidate(
            clusterName,
            providerConfig);
    }

    public async Task<CleanRoomCluster?> GetCluster(
        string clusterName,
        JsonObject? providerConfig)
    {
        return await this.clusterProvider.TryGetCluster(
            clusterName,
            providerConfig);
    }

    public async Task<CleanRoomClusterKubeConfig?> GetClusterKubeConfig(
        string clusterName,
        JsonObject? providerConfig,
        KubeConfigAccessRole accessRole)
    {
        return await this.clusterProvider.TryGetClusterKubeConfig(
            clusterName,
            providerConfig,
            accessRole);
    }

    public async Task<CleanRoomClusterHealth?> GetClusterHealth(
        string clusterName,
        JsonObject? providerConfig)
    {
        return await this.clusterProvider.TryGetClusterHealth(
            clusterName,
            providerConfig);
    }

    public ODataError? DeleteClusterValidate(
        string clusterName,
        JsonObject? providerConfig)
    {
        return this.clusterProvider.DeleteClusterValidate(
            clusterName,
            providerConfig);
    }

    public async Task DeleteCluster(
        string clusterName,
        JsonObject? providerConfig)
    {
        await this.clusterProvider.DeleteCluster(
            clusterName,
            providerConfig);
    }

    public async Task<AnalyticsWorkloadGeneratedDeployment> GenerateAnalyticsWorkloadDeployment(
        GenerateAnalyticsWorkloadDeploymentInput input,
        JsonObject? providerConfig)
    {
        return await this.clusterProvider.GenerateAnalyticsWorkloadDeployment(
            input,
            providerConfig);
    }

    public async Task<KServeInferencingWorkloadGeneratedDeployment>
        GenerateKServeInferencingWorkloadDeployment(
        GenerateKServeInferencingWorkloadDeploymentInput input,
        JsonObject? providerConfig)
    {
        return await this.clusterProvider.GenerateKServeInferencingWorkloadDeployment(
            input,
            providerConfig);
    }
}