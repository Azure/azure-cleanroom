// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;

namespace FrontendSvc.Models.CCF;

/// <summary>
/// Validates if the required secrets and configurations are present based on the encryption mode.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class RequireAWSSecretForAWSS3Attribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext)
    {
        if (value is not DatasetStore store)
        {
            return ValidationResult.Success;
        }

        if (store.StorageAccountType == ResourceType.Aws_S3)
        {
            if (string.IsNullOrWhiteSpace(store.AWSCgsSecretId))
            {
                return new ValidationResult(
                    "AWS CGS Secret is required for AWS S3 stores.");
            }
        }

        return ValidationResult.Success;
    }
}
