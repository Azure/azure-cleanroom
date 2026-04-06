// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcfConsortiumMgr.Clients.Governance.Models;
using CoseUtils;

namespace CcfConsortiumMgr.Clients.Governance;

internal class GovernanceClient : IGovernanceClient
{
    private const string GovApiVersion = "2024-07-01";

    private readonly ILogger logger;
    private readonly HttpClient httpClient;
    private readonly CoseSignKey coseSignKey;

    public GovernanceClient(
        ILogger logger,
        HttpClient httpClient,
        string signingCert,
        string signingKey)
    {
        this.logger = logger;
        this.httpClient = httpClient;

        this.coseSignKey = new CoseSignKey(signingCert, signingKey);
        this.MemberId =
            X509Certificate2.CreateFromPem(this.coseSignKey.Certificate)
            .GetCertHashString(HashAlgorithmName.SHA256)
            .ToLower();
    }

    public string MemberId { get; internal set; }

    public async Task<JsonObject> GetStateDigestUpdate()
    {
        return await this.SendGovMessage<JsonObject>(
            $"gov/members/state-digests/{this.MemberId}:update",
            GovMessageType.StateDigest);
    }

    public async Task AckStateDigest(JsonObject stateDigest)
    {
        await this.SendGovMessage(
            $"gov/members/state-digests/{this.MemberId}:ack",
            GovMessageType.Ack,
            stateDigest);
    }

    public async Task<Members> GetMembers()
    {
        return await this.SendAppMessage<Members>(
            "gov/service/members",
            httpMethod: HttpMethod.Get);
    }

    public async Task<EncryptedShareData> GetMemberEncryptedShare()
    {
        return await this.SendAppMessage<EncryptedShareData>(
            $"/gov/recovery/encrypted-shares/{this.MemberId}",
            httpMethod: HttpMethod.Get,
            skipOutputLogging: true);
    }

    public async Task PerformMemberRecovery(string decryptedShare)
    {
        await this.SendGovMessage<JsonObject>(
            $"gov/recovery/members/{this.MemberId}:recover",
            GovMessageType.RecoveryShare,
            messageContent: new JsonObject()
            {
                ["share"] = decryptedShare
            });
    }

    public async Task<Invitation> GetInvitation(string invitationId)
    {
        return await this.SendAppMessage<Invitation>(
            $"app/users/invitations/{invitationId}",
            httpMethod: HttpMethod.Post);
    }

    public async Task<string> CreateUserIdentityProposal(UserIdentity userIdentity)
    {
        var data = new JsonObject();
        data.Add("tenantId", userIdentity.TenantId);
        if (userIdentity.Identifier != null)
        {
            data.Add("identifier", userIdentity.Identifier);
        }

        JsonObject response =
            await this.SendGovMessage<JsonObject>(
                $"gov/members/proposals:create",
                GovMessageType.Proposal,
                messageContent: new JsonObject()
                {
                    ["actions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "set_user_identity",
                            ["args"] = new JsonObject
                            {
                                ["id"] = userIdentity.ObjectId,
                                ["accountType"] = userIdentity.AccountType.ToString(),
                                ["invitationId"] = userIdentity.InvitationId,
                                ["data"] = data
                            }
                        }
                    }
                });
        return response["proposalId"]!.GetValue<string>();
    }

    public async Task<string> CreateUserInvitationProposal(UserInvitation userInvitation)
    {
        var claims = new JsonObject();
        if (!string.IsNullOrEmpty(userInvitation.TenantId))
        {
            claims.Add("tid", userInvitation.TenantId);
        }

        claims.Add(
            userInvitation.IdentityType == IdentityType.User ?
                "preferred_username" :
                "appid",
            userInvitation.IdentityName);

        JsonObject response =
            await this.SendGovMessage<JsonObject>(
                $"gov/members/proposals:create",
                GovMessageType.Proposal,
                messageContent: new JsonObject()
                {
                    ["actions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "set_user_invitation_by_jwt_claims",
                            ["args"] = new JsonObject
                            {
                                ["invitationId"] = userInvitation.InvitationId,
                                ["type"] = "add",
                                ["accountType"] = userInvitation.AccountType.ToString(),
                                ["claims"] = claims
                            }
                        }
                    }
                });
        return response["proposalId"]!.GetValue<string>();
    }

    public async Task CreateContract(string contractId, JsonObject contractData)
    {
        await this.SendAppMessage(
            $"app/contracts/{contractId}",
            messageContent: new JsonObject()
            {
                ["data"] = JsonSerializer.Serialize(contractData)
            },
            httpMethod: HttpMethod.Put);
    }

    public async Task<string> CreateContractProposal(string contractId, JsonObject contract)
    {
        JsonObject response =
            await this.SendGovMessage<JsonObject>(
                $"gov/members/proposals:create",
                GovMessageType.Proposal,
                messageContent: new JsonObject()
                {
                    ["actions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "set_contract",
                            ["args"] = new JsonObject
                            {
                                ["contractId"] = contractId,
                                ["contract"] = contract
                            }
                        }
                    }
                });
        return response["proposalId"]!.GetValue<string>();
    }

    public async Task<string> CreateDeploymentSpecProposal(
        string contractId,
        JsonObject deploymentSpec)
    {
        JsonObject response =
            await this.SendGovMessage<JsonObject>(
                $"gov/members/proposals:create",
                GovMessageType.Proposal,
                messageContent: new JsonObject()
                {
                    ["actions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "set_deployment_spec",
                            ["args"] = new JsonObject
                            {
                                ["contractId"] = contractId,
                                ["spec"] = new JsonObject
                                {
                                    ["data"] = deploymentSpec
                                }
                            }
                        }
                    }
                });
        return response["proposalId"]!.GetValue<string>();
    }

    public async Task<string> CreateDeploymentInfoProposal(
        string contractId,
        JsonObject deploymentInfo)
    {
        JsonObject response =
            await this.SendGovMessage<JsonObject>(
                $"gov/members/proposals:create",
                GovMessageType.Proposal,
                messageContent: new JsonObject()
                {
                    ["actions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "set_deployment_info",
                            ["args"] = new JsonObject
                            {
                                ["contractId"] = contractId,
                                ["info"] = new JsonObject
                                {
                                    ["data"] = deploymentInfo
                                }
                            }
                        }
                    }
                });
        return response["proposalId"]!.GetValue<string>();
    }

    public async Task<string> CreateCleanRoomPolicyProposal(
        string contractId,
        JsonObject cleanRoomPolicy)
    {
        JsonObject response =
            await this.SendGovMessage<JsonObject>(
                $"gov/members/proposals:create",
                GovMessageType.Proposal,
                messageContent: new JsonObject()
                {
                    ["actions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "set_clean_room_policy",
                            ["args"] = new JsonObject
                            {
                                ["contractId"] = contractId,
                                ["type"] = cleanRoomPolicy["type"]?.ToString(),
                                ["claims"] = cleanRoomPolicy["claims"]?.DeepClone()
                            }
                        }
                    }
                });
        return response["proposalId"]!.GetValue<string>();
    }

    public async Task<string> CreateEnableCAProposal(string contractId)
    {
        JsonObject response =
            await this.SendGovMessage<JsonObject>(
                $"gov/members/proposals:create",
                GovMessageType.Proposal,
                messageContent: new JsonObject()
                {
                    ["actions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "enable_ca",
                            ["args"] = new JsonObject
                            {
                                ["contractId"] = contractId
                            }
                        }
                    }
                });
        return response["proposalId"]!.GetValue<string>();
    }

    public async Task GenerateContractSigningKey(string contractId)
    {
        await this.SendAppMessage(
            $"app/contracts/{contractId}/ca/generateSigningKey");
    }

    public async Task GenerateOidcSigningKey()
    {
        await this.SendAppMessage("app/oidc/generateSigningKey");
    }

    public async Task<Proposal> GetProposal(string proposalId)
    {
        return await this.SendAppMessage<Proposal>(
            $"gov/members/proposals/{proposalId}",
            httpMethod: HttpMethod.Get);
    }

    public async Task VoteProposal(string proposalId, JsonObject ballot)
    {
        await this.SendGovMessage(
            $"gov/members/proposals/{proposalId}/ballots/{this.MemberId}:submit",
            GovMessageType.Ballot,
            ballot,
            proposalId);
    }

    private async Task<T> SendGovMessage<T>(
        string messagePath,
        GovMessageType messageType,
        JsonObject? messageContent = null,
        string? proposalId = null,
        bool skipOutputLogging = false)
    {
        messagePath += $"?api-version={GovApiVersion}";
        byte[] payload =
            await Cose.CreateGovCoseSign1Message(
                this.coseSignKey,
                messageType,
                messageContent?.ToJsonString(),
                proposalId);

        return await this.httpClient.SendCoseRequest<T>(
            this.logger,
            messagePath,
            payload,
            skipOutputLogging);
    }

    private async Task SendGovMessage(
        string messagePath,
        GovMessageType messageType,
        JsonObject? messageContent = null,
        string? proposalId = null,
        bool skipOutputLogging = false)
    {
        messagePath += $"?api-version={GovApiVersion}";
        byte[] payload =
            await Cose.CreateGovCoseSign1Message(
                this.coseSignKey,
                messageType,
                messageContent?.ToJsonString(),
                proposalId);

        await this.httpClient.SendCoseRequest(
            this.logger,
            messagePath,
            payload,
            skipOutputLogging);
    }

    private async Task<T> SendAppMessage<T>(
        string messagePath,
        JsonObject? messageContent = null,
        HttpMethod? httpMethod = null,
        bool skipOutputLogging = false)
    {
        messagePath += $"?api-version={GovApiVersion}";

        return await this.httpClient.SendRequest<T>(
            this.logger,
            messagePath,
            messageContent,
            httpMethod,
            skipOutputLogging);
    }

    private async Task SendAppMessage(
        string messagePath,
        JsonObject? messageContent = null,
        HttpMethod? httpMethod = null,
        bool skipOutputLogging = false)
    {
        messagePath += $"?api-version={GovApiVersion}";

        await this.httpClient.SendRequest(
            this.logger,
            messagePath,
            messageContent,
            httpMethod,
            skipOutputLogging);
    }
}
