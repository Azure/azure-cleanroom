// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class SecretValueRequest
{
    public required string SecretConfig { get; set; }
}