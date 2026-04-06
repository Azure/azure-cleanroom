// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Controllers;

public class SetSnpMinimumTcbVersionInput
{
    public string InfraType { get; set; } = default!;

    /// <summary>
    /// Gets or sets the CPUID hex string identifying the CPU model.
    /// Milan: "00a00f11", Genoa: "00a10f11", Turin: "00b00f21".
    /// </summary>
    public string Cpuid { get; set; } = default!;

    /// <summary>
    /// Gets or sets the TCB version as a lower-case hex string.
    /// For Milan/Genoa format: microcode(1) | snp(1) | reserved(4) | tee(1) | bootloader(1).
    /// For Turin format: microcode(1) | reserved(3) | snp(1) | tee(1) | bootloader(1) | fmc(1).
    /// </summary>
    public string TcbVersion { get; set; } = default!;

    public JsonObject? ProviderConfig { get; set; }
}
