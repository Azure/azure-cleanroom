// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class AadProfileInput
{
    public bool Enabled { get; set; }

    public List<string>? AdminGroupObjectIds { get; set; }
}
