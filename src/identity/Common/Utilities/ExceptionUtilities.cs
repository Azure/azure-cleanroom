// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Controllers;

namespace Utilities;

public static class ExceptionUtilities
{
    public static ExceptionDimensions GetDimensions(this Exception ex)
    {
        string errorCode;

        // Try to get richer data for dimension values if possible.
        if (ex is ApiException identityException)
        {
            errorCode = identityException.Code;
        }
        else
        {
            errorCode = ex.GetType().Name;
        }

        return new ExceptionDimensions
        {
            ErrorCode = errorCode
        };
    }
}

public class ExceptionDimensions
{
    public string ErrorCode { get; set; } = default!;
}