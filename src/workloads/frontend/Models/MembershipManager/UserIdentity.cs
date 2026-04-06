// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class UserIdentity
{
    public required string TenantId { get; set; }

    public required string ObjectId { get; set; }

    public required string AccountType { get; set; }
}