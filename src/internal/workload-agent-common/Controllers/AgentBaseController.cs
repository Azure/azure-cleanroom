// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public abstract class AgentBaseController : ControllerBase
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public AgentBaseController(
        ILogger logger,
        IConfiguration configuration,
        ActiveUserChecker activeUserChecker,
        GovernanceClientManager governanceClientManager)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.ActiveUserChecker = activeUserChecker;
        this.GovernanceClientManager = governanceClientManager;
    }

    public ActiveUserChecker ActiveUserChecker { get; }

    public GovernanceClientManager GovernanceClientManager { get; }

    protected async Task CheckCallerAuthorized(bool useCache = false)
    {
        await this.ActiveUserChecker.CheckActive(this.Request, useCache);
    }

    protected async Task CheckConsortiumMembership()
    {
        var govClient = this.GovernanceClientManager.GetClient();
        using var response = (await govClient.PostAsync($"/members", content: null))!;
        await response.ValidateStatusCodeAsync(this.logger);
        var currentMembers = (await response.Content.ReadFromJsonAsync<Ccf.MemberInfoList>())!;
        HashSet<string> expectedMemberIds;
        var encodedExpectedMembers =
            this.configuration[AgentSettingName.CcfNetworkRecoveryMembers];
        if (string.IsNullOrEmpty(encodedExpectedMembers))
        {
            expectedMemberIds = [];
        }
        else
        {
            var expectedMemberJson = Encoding.UTF8.GetString(
                Convert.FromBase64String(encodedExpectedMembers));
            var expectedMembers = JsonSerializer.Deserialize<List<string>>(expectedMemberJson)!;
            expectedMemberIds = expectedMembers.ToHashSet();
        }

        var currentMemberIds = currentMembers.Value
            .Where(Ccf.IsRecoveryMember)
            .Select(m => m.MemberId).ToHashSet();
        if (expectedMemberIds.SetEquals(currentMemberIds))
        {
            return;
        }

        var missingMembers = expectedMemberIds.Except(currentMemberIds).ToList();
        var extraMembers = currentMemberIds.Except(expectedMemberIds).ToList();
        var errorMessage = new StringBuilder("Consortium recovery membership mismatch. " +
            $"Expected members: [{string.Join(", ", expectedMemberIds)}].");

        if (missingMembers.Any())
        {
            errorMessage.Append($" Missing recovery members: " +
                $"[{string.Join(", ", missingMembers)}].");
        }

        if (extraMembers.Any())
        {
            errorMessage.Append($" Extra recovery members: [{string.Join(", ", extraMembers)}].");
        }

        throw new ApiException(
            HttpStatusCode.Conflict,
            new ODataError(
                code: "ConsortiumRecoveryMembershipMismatch",
                message: errorMessage.ToString()));
    }

    protected async Task<UserDocument<TResult>> GetUserDocument<TResult>(string documentId)
        where TResult : class
    {
        var govClient = this.GovernanceClientManager.GetClient();
        using var userDocumentResponse =
            (await govClient.PostAsync($"/userdocuments/{documentId}", content: null))!;
        await userDocumentResponse.ValidateStatusCodeAsync(this.logger);
        var userDocumentJson =
            (await userDocumentResponse.Content.ReadFromJsonAsync<JsonObject>())!;

        var userDocument = JsonSerializer.Deserialize<UserDocument<TResult>>(
            userDocumentJson,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            })!;

        if (userDocument.RawData == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "ContentEmpty",
                    message: $"Document with Id {documentId} has null content."));
        }

        userDocument.Data = JsonSerializer.Deserialize<TResult>(
            userDocument.RawData,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;

        var contractId = userDocument.ContractId;
        if (string.IsNullOrEmpty(contractId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "ContractIdMissing",
                    message: $"Document with Id {documentId} has no contractId specified."));
        }

        return userDocument;
    }

    protected async Task<StatusWithReason> GetRuntimeConsent(string documentId, string option)
    {
        var govClient = this.GovernanceClientManager.GetClient();
        using var response = (await govClient.PostAsync(
            $"/userdocuments/{documentId}/consentcheck/{option}",
            content: null))!;
        await response.ValidateStatusCodeAsync(this.logger);
        var status =
            (await response.Content.ReadFromJsonAsync<StatusWithReason>())!;
        return status;
    }
}
