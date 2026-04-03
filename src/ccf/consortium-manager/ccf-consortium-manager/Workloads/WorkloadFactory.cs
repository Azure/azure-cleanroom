// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Models;
using CcfConsortiumMgr.Workloads.Analytics;

namespace CcfConsortiumMgr.Workloads;

public class WorkloadFactory : IWorkloadFactory
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public WorkloadFactory(ILogger logger, IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public IWorkload GetWorkload(WorkloadType workloadType)
    {
        switch (workloadType)
        {
            case WorkloadType.Analytics:
                return new AnalyticsWorkload(this.logger, this.configuration);

            default:
                throw new NotSupportedException($"{workloadType} is not supported.");
        }
    }
}
