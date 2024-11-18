﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class WorkspaceConfigurationModel
{
    public IFormFile SigningCertPemFile { get; set; } = default!;

    public IFormFile SigningKeyPemFile { get; set; } = default!;

    public IFormFile? ServiceCertPemFile { get; set; } = default!;

    public string? CcfEndpoint { get; set; } = default!;
}