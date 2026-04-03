// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Controllers;

namespace CcfConsortiumMgr.Models;

public class UserIdentity
{
    public required string TenantId { get; set; }

    public required string ObjectId { get; set; }

    public required AccountType AccountType { get; set; }

    public void Validate()
    {
        if (string.IsNullOrEmpty(this.TenantId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.TenantId)} is missing.");
        }

        if (string.IsNullOrEmpty(this.ObjectId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.ObjectId)} is missing.");
        }
    }
}
