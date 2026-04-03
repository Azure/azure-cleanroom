// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Ballot
{
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable SA1602 // Enumeration items should be documented
    Accepted,

    Rejected,
#pragma warning restore SA1602 // Enumeration items should be documented
#pragma warning restore SA1300 // Element should begin with upper-case letter
}

public record UserDocument<TResult>
    where TResult : class
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("version")]
    public string Version { get; init; } = default!;

    [JsonPropertyName("approvers")]
    public List<Approver> Approvers { get; init; } = default!;

    [JsonPropertyName("state")]
    public string State { get; init; } = default!;

    [JsonPropertyName("data")]
    public string RawData { get; init; } = default!;

    [JsonPropertyName("contractId")]
    public string ContractId { get; init; } = default!;

    [JsonPropertyName("proposalId")]
    public string ProposalId { get; init; } = default!;

    [JsonPropertyName("proposerId")]
    public string ProposerId { get; init; } = default!;

    [JsonPropertyName("finalVotes")]
    public List<Votes> FinalVotes { get; init; } = default!;

    [JsonIgnore]
    public TResult Data { get; set; } = default!;
}

public record Approver(
    [property: JsonPropertyName("approverId")] string ApproverId,

    [property: JsonPropertyName("approverIdType")] string ApproverIdType);

public record Votes(
    [property: JsonPropertyName("approverId")] string ApproverId,

    [property: JsonPropertyName("ballot")] Ballot Ballot);