// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfProvider;

public class CcfNetworkRecoveryAgents
{
    public string Endpoint { get; set; } = default!;

    public List<CcfRecoveryAgent> Agents { get; set; } = default!;
}