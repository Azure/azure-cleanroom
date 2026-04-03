// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Models;

public class ConsentStatusResponse
{
    public required string Status { get; set; }

    public ConsentStatusReason? Reason { get; set; }
}

public class ConsentStatusReason
{
    public required string Code { get; set; }

    public required string Message { get; set; }
}