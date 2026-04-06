// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class SecurityPolicyConfiguration
{
    // The base64 encoded confidential compute enforcement policy for a caci deployment.
    // Goes along with SecurityPolicyCreationOption.userSuppliedPolicy.
    public string? Policy { get; set; } = default!;

    public SecurityPolicyCreationOption PolicyCreationOption { get; set; } = default!;
}