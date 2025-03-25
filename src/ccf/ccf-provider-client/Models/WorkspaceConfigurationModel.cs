// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class WorkspaceConfigurationModel
{
    public IFormFile? SigningCertPemFile { get; set; } = default!;

    public IFormFile? SigningKeyPemFile { get; set; } = default!;

    public string? SigningCertId { get; set; } = default!;
}