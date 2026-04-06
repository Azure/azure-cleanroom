// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Models;

public class GetContractResponse
{
    public string Id { get; set; } = default!;

    public string Version { get; set; } = default!;

    public string State { get; set; } = default!;

    // Data is a JSON string matching the ContractData structure.
    public string Data { get; set; } = default!;

    public string ProposalId { get; set; } = default!;

    public FinalVote[] FinalVotes { get; set; } = default!;
}

public class ContractData
{
    public string? CcrgovEndpoint { get; set; }

    public string? CcrgovApiPathPrefix { get; set; }

    public CcrgovServiceCertDiscovery? CcrgovServiceCertDiscovery { get; set; }

    public object[]? CcfNetworkRecoveryMembers { get; set; }
}

public class CcrgovServiceCertDiscovery
{
    public string? Endpoint { get; set; }

    public string? SnpHostData { get; set; }

    public string? ConstitutionDigest { get; set; }

    public string? JsappBundleDigest { get; set; }
}

public class FinalVote
{
    public string MemberId { get; set; } = default!;

    public bool Vote { get; set; } = default!;
}
