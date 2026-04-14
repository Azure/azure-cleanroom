// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;

namespace FrontendSvc.Models.CCF;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public class RequiredNotNullOrWhiteSpaceAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext)
    {
        if (value is not string str || string.IsNullOrWhiteSpace(str))
        {
            return new ValidationResult(
                $"{validationContext.DisplayName} must not be null, empty, or whitespace.",
                new[] { validationContext.MemberName! });
        }

        return ValidationResult.Success;
    }
}