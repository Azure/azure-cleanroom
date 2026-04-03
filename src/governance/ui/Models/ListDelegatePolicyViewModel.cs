// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace CgsUI.Models;

public class ListDelegatePolicyViewModel
{
    public string DelegateType { get; set; } = default!;

    public string DelegateId { get; set; } = default!;
}
