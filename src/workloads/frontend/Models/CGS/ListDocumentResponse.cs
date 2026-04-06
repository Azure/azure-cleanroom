// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class ListDocumentResponse
{
    public List<DocumentItem> Value { get; set; } = new();
}

public class DocumentItem
{
    public string Id { get; set; } = default!;

    public Dictionary<string, string> Labels { get; set; } = new();
}