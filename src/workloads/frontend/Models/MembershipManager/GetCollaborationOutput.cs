// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class GetCollaborationOutput : CollaborationOutput
{
    public required string ConsortiumEndpoint { get; set; }

    public required string ConsortiumServiceCertificatePem { get; set; }
}