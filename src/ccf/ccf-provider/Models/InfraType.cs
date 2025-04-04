﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfProvider;

#pragma warning disable SA1300 // Element should begin with upper-case letter
public enum InfraType
{
    /// <summary>
    /// CCF nodes are started in Confidential ACI instances ie in n SEV-SNP environment.
    /// Meant for production.
    /// </summary>
    caci,

    /// <summary>
    /// CCF nodes are started in Docker containers. Meant for local dev/test.
    /// </summary>
    @virtual,

    /// <summary>
    /// CCF nodes are started in standard ACI deployment. Meant for dev/test where hosting the CCF
    /// network in Azure is required.
    /// </summary>
    virtualaci
}
#pragma warning restore SA1300 // Element should begin with upper-case letter
