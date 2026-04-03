// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace CgsUI.Models;

public class DelegatePolicyViewModel
{
    public JsonObject Claims { get; set; } = default!;
}
