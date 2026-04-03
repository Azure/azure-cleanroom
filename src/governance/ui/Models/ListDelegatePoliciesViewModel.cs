// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace CgsUI.Models;

public class ListDelegatePoliciesViewModel
{
    public string ContractId { get; set; } = default!;

    public List<ListDelegatePolicyViewModel> Value { get; set; } = default!;
}
