// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

#pragma warning disable SA1300 // Element should begin with upper-case letter

public enum SecurityPolicyCreationOption
{
#pragma warning disable SA1602 // Enumeration items should be documented
    cached,
    cachedDebug,
    allowAll,
    userSupplied
#pragma warning restore SA1602 // Enumeration items should be documented
}
#pragma warning restore SA1300 // Element should begin with upper-case letter
