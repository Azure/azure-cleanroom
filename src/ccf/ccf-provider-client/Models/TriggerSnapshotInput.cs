﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Controllers;

public class TriggerSnapshotInput
{
    public string InfraType { get; set; } = default!;

    public JsonObject? ProviderConfig { get; set; }
}