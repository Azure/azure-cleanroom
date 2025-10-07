// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcfCommon;
using Microsoft.Extensions.Logging;

namespace CcfConsortiumMgrProvider;

public class CcfConsortiumManagerProvider
{
    private ILogger logger;
    private ICcfConsortiumManagerInstanceProvider instanceProvider;

    public CcfConsortiumManagerProvider(
        ILogger logger,
        ICcfConsortiumManagerInstanceProvider instanceProvider)
    {
        this.logger = logger;
        this.instanceProvider = instanceProvider;
    }

    public async Task<CcfConsortiumManager> CreateConsortiumManager(
        string consortiumManagerName,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig)
    {
        var instanceName = "cm-" + consortiumManagerName + "-0";
        var cmEndpoint =
            await this.instanceProvider.CreateConsortiumManager(
                instanceName,
                consortiumManagerName,
                policyOption,
                providerConfig);

        this.logger.LogInformation(
            $"Consortium manager endpoint is up at: {cmEndpoint.Endpoint}.");
        return new CcfConsortiumManager
        {
            Name = consortiumManagerName,
            InfraType = this.instanceProvider.InfraType.ToString(),
            Endpoint = cmEndpoint.Endpoint,
            ServiceCert = "NA" // TODO (devbabu): Implement ServiceCert.
        };
    }

    public async Task<CcfConsortiumManager?> GetConsortiumManager(
        string consortiumManagerName,
        JsonObject? providerConfig)
    {
        var cmEndpoint =
            await this.instanceProvider.TryGetConsortiumManagerEndpoint(
                consortiumManagerName,
                providerConfig);
        if (cmEndpoint != null)
        {
            return new CcfConsortiumManager
            {
                Name = consortiumManagerName,
                InfraType = this.instanceProvider.InfraType.ToString(),
                Endpoint = cmEndpoint.Endpoint,
                ServiceCert = "NA" // TODO (devbabu): Implement ServiceCert.
            };
        }

        return null;
    }
}
