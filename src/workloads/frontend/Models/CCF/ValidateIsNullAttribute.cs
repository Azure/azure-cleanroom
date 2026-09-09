// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;

namespace FrontendSvc.Models.CCF;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public class ValidateIsNullAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext)
    {
        if (value != null)
        {
            return new ValidationResult(
                $"{validationContext.DisplayName} is no longer supported and must not be set.",
                [validationContext.MemberName!]);
        }

        return ValidationResult.Success;
    }
}
