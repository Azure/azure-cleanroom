// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;

namespace FrontendSvc.Models.CCF;

/// <summary>
/// Validates if the required secrets and configurations are present based on the encryption mode.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class RequiredSecretsAndConfigurationsAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext)
    {
        if (value is not DatasetDetails details)
        {
            return ValidationResult.Success;
        }

        if (details.Store.EncryptionMode == EncryptionMode.CSE ||
            details.Store.EncryptionMode == EncryptionMode.CPK)
        {
            if (details.Dek is null || details.Kek is null)
            {
                return new ValidationResult(
                    "Dek & Kek details are required for encryption mode CSE and CPK.");
            }

            if (details.Store.EncryptionMode == EncryptionMode.CPK &&
                string.IsNullOrWhiteSpace(details.Kek.MaaUrl))
            {
                return new ValidationResult(
                    "MaaUrl is required for Kek details for encryption mode CPK.");
            }
        }

        return ValidationResult.Success;
    }
}
