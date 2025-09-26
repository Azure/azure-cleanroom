// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CgsUI.Models;

public class SecretsViewModel
{
    public string ContractId { get; set; } = default!;

    public Secret[] Value { get; set; } = default!;
}

public class Secret
{
    public string SecretId { get; set; } = default!;
}
