// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfConsortiumMgrProvider;

#pragma warning disable SA1300 // Element should begin with upper-case letter
public enum CMInfraType
{
    /// <summary>
    /// Consortium manager is started in Confidential ACI instances ie in an SEV-SNP environment.
    /// Meant for production.
    /// </summary>
    caci,

    /// <summary>
    /// Consortium manager is started in Docker containers. Meant for local dev/test.
    /// </summary>
    @virtual,
}
#pragma warning restore SA1300 // Element should begin with upper-case letter
