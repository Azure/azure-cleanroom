// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CgsUI.Models;

public class CreateUserDocumentViewModel
{
    public UserDocument Document { get; set; } = new();

    public List<string> AvailableContracts { get; set; } = new();

    public List<LabelKeyValue> Labels { get; set; } = new();

    public class LabelKeyValue
    {
        public string Key { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }
}
