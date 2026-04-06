// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class UserProposalApprover
{
    [JsonPropertyName("approverId")]
    public string ApproverId { get; set; } = default!;

    [JsonPropertyName("approverIdType")]
    public string ApproverIdType { get; set; } = default!;
}
