// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Controllers;

public class RemoveSnpMinimumTcbVersionInput
{
    public string InfraType { get; set; } = default!;

    /// <summary>
    /// Gets or sets the CPUID hex string identifying the CPU model to remove.
    /// Milan: "00a00f11", Genoa: "00a10f11", Turin: "00b00f21".
    /// </summary>
    public string Cpuid { get; set; } = default!;

    public JsonObject? ProviderConfig { get; set; }
}
