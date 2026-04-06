// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AttestationClient;
using CcfCommon;
using CcfConsortiumMgr.Clients;
using CcfConsortiumMgr.Clients.Governance;
using CcfConsortiumMgr.Clients.Node;
using CcfConsortiumMgr.Clients.Node.Models;
using CcfConsortiumMgr.Clients.RecoveryAgent.Models;
using CcfConsortiumMgr.Models;
using CcfConsortiumMgr.Utils;
using CcfConsortiumMgr.Workloads;
using Controllers;

namespace CcfConsortiumMgr;

public class CcfConsortiumManager
{
    private const string ConsortiumManagerMemberName = "consortium-manager";

    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly IMemberStore memberStore;
    private readonly ClientManager clientManager;
    private readonly IWorkloadFactory workloadFactory;
    private readonly string serviceEndpoint;
    private readonly string serviceCertLocation;

    public CcfConsortiumManager(
        ILogger logger,
        IConfiguration configuration,
        IMemberStore memberStore,
        ClientManager clientManager,
        IWorkloadFactory workloadFactory)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.memberStore = memberStore;
        this.clientManager = clientManager;
        this.workloadFactory = workloadFactory;
        this.serviceEndpoint = configuration[SettingName.ServiceEndpoint]!;
        this.serviceCertLocation =
            configuration[SettingName.ServiceCertLocation] ??
            MountPaths.ConsortiumManagerCertPemFile;
    }

    public async Task<ConsortiumManagerReport> GetReport()
    {
        if (!Path.Exists(this.serviceCertLocation))
        {
            throw new ApiException(
                HttpStatusCode.ServiceUnavailable,
                "ConsortiumManagerCertNotFound",
                "Could not locate the service certificate for the consortium manager.");
        }

        var serviceCert = await File.ReadAllTextAsync(this.serviceCertLocation);

        string platform;
        SnpCACIAttestationReport? report = null;
        if (Attestation.IsSnpCACI())
        {
            platform = "snp";
            var bytes = Encoding.UTF8.GetBytes(serviceCert);
            report = (await Attestation.GetCACIReportAsync(bytes)).SnpCaci;
        }
        else
        {
            platform = "virtual";
        }

        string hostData = await Attestation.GetCACIHostData();
        return new ConsortiumManagerReport
        {
            Platform = platform,
            Report = report,
            ServiceCert = serviceCert,
            HostData = hostData
        };
    }

    public async Task GenerateKeys()
    {
        // TODO (devbabu): Validate that the hostData of the current deployment is valid by
        // checking against a published list.
        // TODO (devbabu): ConsortiumManagerMemberName/IMemberStore would need changes to
        // accommodate different service versions.
        await this.memberStore.GenerateSigningKey(ConsortiumManagerMemberName);
        await this.memberStore.GenerateEncryptionKey(ConsortiumManagerMemberName);
    }

    public async Task<ConsortiumManagerMember> GetConsortiumManagerMember()
    {
        SigningKeyInfo? signingKeyInfo =
            await this.memberStore.GetSigningKey(ConsortiumManagerMemberName);
        EncryptionKeyInfo? encryptionKeyInfo =
            await this.memberStore.GetEncryptionKey(ConsortiumManagerMemberName);

        return new ConsortiumManagerMember()
        {
            SigningKey = signingKeyInfo!.SigningCert,
            EncryptionKey = encryptionKeyInfo!.EncryptionPublicKey
        };
    }

    public async Task PrepareConsortium(
        string ccfEndpoint,
        string ccfServiceCertPem,
        string recoveryAgentEndpoint,
        string recoveryServiceEndpoint,
        UserIdentity userIdentity)
    {
        ConsortiumManagerMember consortiumManagerMember =
            await this.GetConsortiumManagerMember();
        IGovernanceClient govClient =
            await this.GetGovernanceClient(ccfEndpoint, ccfServiceCertPem);

        // Validate consortium state before proceeding.
        await consortiumManagerMember.ValidateConsortium(
            this.clientManager,
            ccfEndpoint,
            ccfServiceCertPem,
            recoveryAgentEndpoint,
            recoveryServiceEndpoint,
            govClient);

        // Accept Consortium Manager as member now that validation succeeded and
        // proceed to adding user identity.
        await govClient.AcceptMembership();
        await govClient.ProvisionOidcSigningKey();
        await govClient.ProvisionUserIdentity(userIdentity);
    }

    public async Task<string> ValidateConsortium(
        string ccfEndpoint,
        string recoveryAgentEndpoint,
        string recoveryServiceEndpoint)
    {
        INodeClient nodeClient = this.clientManager.GetInsecureNodeClient(ccfEndpoint);
        Network network = await nodeClient.GetNetwork();

        ConsortiumManagerMember consortiumManagerMember =
            await this.GetConsortiumManagerMember();
        IGovernanceClient govClient =
            await this.GetGovernanceClient(ccfEndpoint, network.ServiceCert);
        await consortiumManagerMember.ValidateConsortium(
            this.clientManager,
            ccfEndpoint,
            network.ServiceCert,
            recoveryAgentEndpoint,
            recoveryServiceEndpoint,
            govClient);

        return network.ServiceCert;
    }

    public async Task RecoverConsortium(string ccfEndpoint, string ccfServiceCertPem)
    {
        IGovernanceClient govClient =
            await this.GetGovernanceClient(ccfEndpoint, ccfServiceCertPem);
        EncryptionPrivateKeyInfo encryptionKeyInfo =
            await this.memberStore.ReleaseEncryptionKey(ConsortiumManagerMemberName);

        await govClient.PerformRecovery(encryptionKeyInfo.EncryptionPrivateKey);
    }

    public async Task GenerateWorkloadContract(
        string ccfEndpoint,
        string ccfServiceCertPem,
        string recoveryAgentEndpoint,
        string recoveryServiceEndpoint,
        JsonObject ccfProviderConfig,
        WorkloadType workloadType,
        string contractId,
        string policyCreationOption)
    {
        IWorkload workload = this.workloadFactory.GetWorkload(workloadType);
        ConsortiumManagerMember consortiumManagerMember =
            await this.GetConsortiumManagerMember();
        IGovernanceClient govClient =
            await this.GetGovernanceClient(ccfEndpoint, ccfServiceCertPem);

        // Validate consortium state before proceeding.
        (QuotesList nodeQuotes, NetworkReport networkReport) =
            await consortiumManagerMember.ValidateConsortium(
                this.clientManager,
                ccfEndpoint,
                ccfServiceCertPem,
                recoveryAgentEndpoint,
                recoveryServiceEndpoint,
                govClient);

        // Create and propose workload contract.
        List<Clients.Governance.Models.Member> recoveryMembers =
            await govClient.GetRecoveryMembers();
        var contractData =
            new JsonObject()
            {
                ["ccrgovEndpoint"] = ccfEndpoint,
                ["ccrgovApiPathPrefix"] = $"/app/contracts/{contractId}",
                ["ccrgovServiceCertDiscovery"] = new JsonObject()
                {
                    ["endpoint"] = $"{recoveryAgentEndpoint}/network/report",
                    ["snpHostData"] = nodeQuotes.GetSnpHostData(),
                    ["constitutionDigest"] = networkReport.ConstitutionDigest,
                    ["jsappBundleDigest"] = networkReport.JsAppBundleDigest
                },
                ["ccfNetworkRecoveryMembers"] =
                    new JsonArray([.. recoveryMembers.Select(x => x.MemberId)]),
                ["consortiumManager"] = new JsonObject()
                {
                    ["endpoint"] = $"{this.serviceEndpoint}",
                    ["consortiumValidationEndpoint"] =
                        $"{this.serviceEndpoint}/consortiums/validateConsortium",
                    ["serviceCertDiscovery"] = new JsonObject()
                    {
                        ["endpoint"] = $"{this.serviceEndpoint}/report",

                        // TODO (devbabu): Fix this once the code upgrade flow is in place.
                        ["hostDataUrl"] = "<pathToHostDataFile>"
                    }
                }
            };
        await govClient.ProvisionContract(contractId, contractData);

        // Create and propose deployment spec and policy.
        (JsonObject deploymentSpec, JsonObject cleanRoomPolicy) =
            await workload.GenerateDeploymentSpec(
                contractData,
                policyCreationOption);
        await govClient.ProvisionDeploymentSpec(contractId, deploymentSpec, cleanRoomPolicy);

        // Propose contract signing keys (after enabling CA).
        await govClient.ProvisionSigningKeys(contractId);
    }

    public async Task AddUserInvitation(
        string ccfEndpoint,
        string ccfServiceCertPem,
        UserInvitation userInvitation)
    {
        IGovernanceClient govClient =
            await this.GetGovernanceClient(ccfEndpoint, ccfServiceCertPem);

        await govClient.ProvisionUserInvitation(userInvitation);
    }

    public async Task ActivateUserInvitation(
        string ccfEndpoint,
        string ccfServiceCertPem,
        string invitationId)
    {
        IGovernanceClient govClient =
            await this.GetGovernanceClient(ccfEndpoint, ccfServiceCertPem);

        await govClient.ProvisionUserIdentity(invitationId);
    }

    public async Task AddUserIdentity(
        string ccfEndpoint,
        string ccfServiceCertPem,
        UserIdentity userIdentity)
    {
        IGovernanceClient govClient =
            await this.GetGovernanceClient(ccfEndpoint, ccfServiceCertPem);

        await govClient.ProvisionUserIdentity(userIdentity);
    }

    public async Task SetDeploymentInfo(
        string ccfEndpoint,
        string ccfServiceCertPem,
        string contractId,
        JsonObject deploymentInfo)
    {
        IGovernanceClient govClient =
            await this.GetGovernanceClient(ccfEndpoint, ccfServiceCertPem);

        await govClient.ProvisionDeploymentInfo(contractId, deploymentInfo);
    }

    private async Task<IGovernanceClient> GetGovernanceClient(
        string ccfEndpoint,
        string ccfServiceCertPem)
    {
        SigningPrivateKeyInfo? signingKeyInfo =
            await this.memberStore.ReleaseSigningKey(ConsortiumManagerMemberName);

        IGovernanceClient govClient =
            this.clientManager.GetGovernanceClient(
                ccfEndpoint,
                ccfServiceCertPem,
                signingKeyInfo.SigningCert,
                signingKeyInfo.SigningKey);
        return govClient;
    }
}
