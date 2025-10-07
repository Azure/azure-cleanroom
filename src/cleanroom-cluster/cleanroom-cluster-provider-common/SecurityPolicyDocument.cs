// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class SecurityPolicyDocument
{
    public List<ContainerSection> Containers { get; set; } = default!;

    public string Rego { get; set; } = default!;

    public string RegoDebug { get; set; } = default!;

    public class ContainerSection
    {
        public string Name { get; set; } = default!;

        public string Image { get; set; } = default!;

        public string Digest { get; set; } = default!;
    }
}
