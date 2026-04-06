// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfConsortiumMgrProvider;

public class CcfConsortiumManager
{
    public required string Name { get; set; }

    public required string InfraType { get; set; }

    public required string Endpoint { get; set; }

    public required string ServiceCert { get; set; }
}
