// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record UserIdentities([property: JsonPropertyName("value")] List<UserIdentity> Value);

public record UserIdentity(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accountType")] string AccountType,
    [property: JsonPropertyName("invitationId")] string? InvitationId,
    [property: JsonPropertyName("data")] UserIdentityData Data);

public record UserIdentityData(
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("identifier")] string Identifier);