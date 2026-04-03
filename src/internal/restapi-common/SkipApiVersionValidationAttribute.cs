// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

/// <summary>
/// Marks an action or controller to skip api-version validation.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipApiVersionValidationAttribute : Attribute
{
}
