// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class FlexNodeProfileInput
{
    public bool Enabled { get; set; }

    public string? SshPrivateKeyPem { get; set; }

    public string? SshPublicKey { get; set; }

    /// <summary>
    /// Gets or sets the policy signing certificate PEM used for api-server-proxy pod verification.
    /// </summary>
    public string? PolicySigningCertPem { get; set; }

    /// <summary>
    /// Gets or sets the VM size for the flex node. If not specified, defaults to Standard_DC4as_v5.
    /// </summary>
    public string? VmSize { get; set; }

    /// <summary>
    /// Gets or sets the number of flex nodes to create. Defaults to 1.
    /// </summary>
    public int NodeCount { get; set; } = 1;
}