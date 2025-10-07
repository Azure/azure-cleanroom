// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class SecurityPolicyConfigInput
{
    // The base64 encoded confidential compute enforcement policy for a caci deployment.
    // Goes along with SecurityPolicyCreationOption.userSuppliedPolicy.
    public string? Policy { get; set; } = default!;

    public string PolicyCreationOption { get; set; } = default!;

    public static SecurityPolicyConfiguration Convert(SecurityPolicyConfigInput? input)
    {
        if (input != null)
        {
            SecurityPolicyCreationOption option;
            if (!string.IsNullOrEmpty(input.PolicyCreationOption))
            {
                option = Enum.Parse<SecurityPolicyCreationOption>(
                input.PolicyCreationOption,
                ignoreCase: true);
            }
            else
            {
                option = SecurityPolicyCreationOption.cached;
            }

            return new SecurityPolicyConfiguration
            {
                Policy = input.Policy,
                PolicyCreationOption = option
            };
        }
        else
        {
            return new SecurityPolicyConfiguration
            {
                PolicyCreationOption = SecurityPolicyCreationOption.cached
            };
        }
    }
}