// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace FrontendSvc.Models;

public class CleanroomPolicyResponse
{
    public Dictionary<string, JsonElement> Policy { get; set; } = new();
}