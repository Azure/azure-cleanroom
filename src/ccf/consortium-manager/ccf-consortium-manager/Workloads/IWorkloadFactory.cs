// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Models;

namespace CcfConsortiumMgr.Workloads;

public interface IWorkloadFactory
{
    IWorkload GetWorkload(WorkloadType workloadType);
}
