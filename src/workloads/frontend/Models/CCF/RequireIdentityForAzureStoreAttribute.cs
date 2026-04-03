// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;

namespace FrontendSvc.Models.CCF;

/// <summary>
/// Validates that Identity is not null when StorageAccountType contains "Azure".
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class RequireIdentityForAzureStoreAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext)
    {
        if (value is not DatasetDetails details)
        {
            return ValidationResult.Success;
        }

        string storageType = details.Store.StorageAccountType.ToString();
        if (storageType.Contains("Azure", StringComparison.OrdinalIgnoreCase)
            && details.Identity is null)
        {
            return new ValidationResult(
                "Identity is required when StorageAccountType is an Azure type.",
                [nameof(DatasetDetails.Identity)]);
        }

        return ValidationResult.Success;
    }
}
