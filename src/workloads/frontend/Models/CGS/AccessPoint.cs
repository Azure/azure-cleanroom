// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class AccessPoint
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required AccessPointType Type { get; set; }

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("store")]
    public required Resource Store { get; set; }

    [JsonPropertyName("identity")]
    public Identity? Identity { get; set; }

    [JsonPropertyName("protection")]
    public required PrivacyProxySettings Protection { get; set; }

    public static AccessPoint FromDatasetDetails(
        DatasetDetails datasetDetails)
    {
        return new AccessPoint
        {
            Name = datasetDetails.Name,
            Path = string.Empty,
            Type = GetAccessPointType(
                datasetDetails.DatasetAccessPolicy.AccessMode),
            Store = Resource.FromDatasetStore(
                datasetDetails.Store),
            Identity = Identity.FromDatasetIdentity(
                datasetDetails.Identity),
            Protection = PrivacyProxySettings.FromDatasetDetails(datasetDetails),
        };
    }

    private static AccessPointType GetAccessPointType(AccessMode datasetAccessPolicyAccessMode)
    {
        return datasetAccessPolicyAccessMode == AccessMode.Read
             ? AccessPointType.Volume_ReadOnly
             : AccessPointType.Volume_ReadWrite;
    }
}
