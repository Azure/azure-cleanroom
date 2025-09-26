// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Controllers;

namespace CleanRoomProvider;

public interface ICleanRoomClusterProvider
{
    public InfraType InfraType { get; }

    ODataError? CreateClusterValidate(
        string clusterName,
        JsonObject? providerConfig)
    {
        return null;
    }

    public Task<CleanRoomCluster> CreateCluster(
        string clusterName,
        CleanRoomClusterInput input,
        JsonObject? providerConfig,
        IProgress<string> progressReporter);

    Task<CleanRoomCluster?> UpdateCluster(
        string clusterName,
        CleanRoomClusterInput input,
        JsonObject? providerConfig,
        IProgress<string> progressReporter);

    ODataError? GetClusterValidate(
        string clusterName,
        JsonObject? providerConfig)
    {
        return null;
    }

    public Task<CleanRoomCluster> GetCluster(
        string clusterName,
        JsonObject? providerConfig);

    public Task<CleanRoomClusterKubeConfig?> TryGetClusterKubeConfig(
        string clusterName,
        JsonObject? providerConfig);

    public Task<CleanRoomCluster?> TryGetCluster(
        string clusterName,
        JsonObject? providerConfig);

    ODataError? DeleteClusterValidate(
        string clusterName,
        JsonObject? providerConfig)
    {
        return null;
    }

    public Task DeleteCluster(string clusterName, JsonObject? providerConfig);

    public Task<AnalyticsWorkloadGeneratedDeployment> GenerateAnalyticsWorkloadDeployment(
        GenerateAnalyticsWorkloadDeploymentInput input,
        JsonObject? providerConfig);
}