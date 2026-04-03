// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using AttestationClient;
using CcfConsortiumMgr.Clients;
using CcfConsortiumMgr.Clients.Governance;
using CcfConsortiumMgr.Clients.Governance.Models;
using CcfConsortiumMgr.Clients.Node;
using CcfConsortiumMgr.Clients.Node.Models;
using CcfConsortiumMgr.Clients.RecoveryAgent;
using CcfConsortiumMgr.Clients.RecoveryAgent.Models;
using CcfConsortiumMgr.Clients.RecoveryService;
using CcfConsortiumMgr.Clients.RecoveryService.Models;
using CcfConsortiumMgr.Models;
using Controllers;

namespace CcfConsortiumMgr.Utils;

public static class ValidationUtils
{
    public static async Task<(QuotesList, NetworkReport)> ValidateConsortium(
        this ConsortiumManagerMember consortiumManagerMember,
        ClientManager clientManager,
        string ccfEndpoint,
        string ccfServiceCertPem,
        string recoveryAgentEndpoint,
        string recoveryServiceEndpoint,
        IGovernanceClient govClient)
    {
        INodeClient networkClient =
            clientManager.GetNodeClient(ccfEndpoint, ccfServiceCertPem);
        IRecoveryAgentClient recoveryAgentClient =
            clientManager.GetInsecureRecoveryAgentClient(recoveryAgentEndpoint);
        IRecoveryServiceClient recoveryServiceClient =
            clientManager.GetInsecureRecoveryServiceClient(recoveryServiceEndpoint);

        QuotesList nodeQuotes = await networkClient.GetNodeQuotes();
        NetworkReport networkReport = await recoveryAgentClient.GetNetworkReport();
        RecoveryServiceReport rsReport = await recoveryServiceClient.GetRecoveryServiceReport();

        await ValidateConsortium(
            ccfEndpoint,
            recoveryAgentEndpoint,
            recoveryServiceEndpoint,
            nodeQuotes,
            networkReport,
            rsReport,
            govClient,
            recoveryServiceClient,
            consortiumManagerMember);
        return (nodeQuotes, networkReport);
    }

    private static async Task ValidateConsortium(
        string ccfEndpoint,
        string recoveryAgentEndpoint,
        string recoveryServiceEndpoint,
        QuotesList nodeQuotes,
        NetworkReport networkReport,
        RecoveryServiceReport recoveryServiceReport,
        IGovernanceClient govClient,
        IRecoveryServiceClient recoveryServiceClient,
        ConsortiumManagerMember consortiumManagerMember)
    {
        ValidateEndpoints(ccfEndpoint, recoveryAgentEndpoint, recoveryServiceEndpoint);
        ValidateAttestation(nodeQuotes, networkReport, recoveryServiceReport);

        await ValidateMembers(
            govClient,
            recoveryServiceClient,
            recoveryServiceReport,
            consortiumManagerMember);
    }

    private static void ValidateEndpoints(
        string ccfEndpoint,
        string raEndpoint,
        string rsEndpoint)
    {
        if (!Uri.TryCreate(ccfEndpoint, UriKind.Absolute, out Uri? ccfEndpointUri) ||
            ccfEndpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "InsecureConsortiumEndpoint",
                "Consortium endpoint is not https.");
        }

        if (!Uri.TryCreate(raEndpoint, UriKind.Absolute, out Uri? raEndpointUri) ||
            raEndpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "InsecureRecoveryAgentEndpoint",
                "Recovery agent endpoint is not https.");
        }

        if (!Uri.TryCreate(rsEndpoint, UriKind.Absolute, out Uri? rsEndpointUri) ||
            rsEndpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "InsecureRecoveryServiceEndpoint",
                "Recovery service endpoint is not https.");
        }

        if (ccfEndpointUri.Host != raEndpointUri.Host)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "InvalidConsortiumEndpoint",
                "Consortium and recovery agent are not co-located.");
        }
    }

    private static void ValidateAttestation(
        QuotesList nodeQuotes,
        NetworkReport networkReport,
        RecoveryServiceReport rsReport)
    {
        if (Attestation.IsSnpCACI())
        {
            if (nodeQuotes.Quotes.Count == 0)
            {
                throw new ApiException(
                    HttpStatusCode.PreconditionFailed,
                    "InvalidNodeReport",
                    "Node quotes is empty.");
            }

            if (networkReport?.Report == null)
            {
                throw new ApiException(
                    HttpStatusCode.PreconditionFailed,
                    "InvalidNetworkReport",
                    "Network report is null.");
            }

            if (rsReport?.Report == null)
            {
                throw new ApiException(
                    HttpStatusCode.PreconditionFailed,
                    "InvalidRecoveryServiceReport",
                    "Recovery Service report is null.");
            }

            foreach (NodeQuote nodeQuote in nodeQuotes.Quotes)
            {
                SnpReport.VerifySnpAttestation(
                    nodeQuote.Raw,
                    nodeQuote.Endorsements,
                    uvmEndorsements: null);
            }

            SnpReport.VerifySnpAttestation(
                networkReport.Report.Attestation,
                networkReport.Report.PlatformCertificates,
                networkReport.Report.UvmEndorsements);
            SnpReport.VerifySnpAttestation(
                rsReport.Report.Attestation,
                rsReport.Report.PlatformCertificates,
                rsReport.Report.UvmEndorsements);
        }
    }

    private static async Task ValidateMembers(
        IGovernanceClient govClient,
        IRecoveryServiceClient recoveryServiceClient,
        RecoveryServiceReport recoveryServiceReport,
        ConsortiumManagerMember consortiumManagerMember)
    {
        Members allMembers = await govClient.GetMembers();

        Member? cmMember =
            allMembers.Value
            .SingleOrDefault(x => x.MemberId == govClient.MemberId);
        ValidateConsortiumManagerMember(cmMember, consortiumManagerMember);

        IEnumerable<Member> otherMembers =
            allMembers.Value
            .Where(x => x.MemberId != govClient.MemberId);
        foreach (Member otherMember in otherMembers)
        {
            if (otherMember.MemberData.IsOperator)
            {
                ValidateOperatorMember(otherMember);
            }
            else if (otherMember.MemberData.IsRecoveryOperator)
            {
                await ValidateRecoveryOperatorMember(
                    otherMember,
                    recoveryServiceReport,
                    recoveryServiceClient);
            }
            else
            {
                string nonOperatorMembers =
                    string.Join(
                        ", ",
                        allMembers.Value
                        .Where(x => x.MemberId != govClient.MemberId)
                        .Where(x => !x.MemberData.IsOperator && !x.MemberData.IsRecoveryOperator)
                        .Select(x => x.MemberData.Identifier));
                throw new ApiException(
                    HttpStatusCode.PreconditionFailed,
                    "ConsortiumHasOtherMembers",
                    $"Consortium has members other than Consortium Manager: {nonOperatorMembers}.");
            }
        }
    }

    private static void ValidateConsortiumManagerMember(
        Member? member,
        ConsortiumManagerMember cmMember)
    {
        if (member == null)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConsortiumManagerIsNotMember",
                "Consortium Manager member was not found.");
        }

        if (member.MemberData.IsOperator || member.MemberData.IsRecoveryOperator)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConsortiumManagerIsNotMember",
                "Consortium Manager member is marked as operator.");
        }

        if (member.RecoveryRole != "Owner")
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConsortiumManagerIsNotRecoveryOwner",
                $"Consortium Manager member is not recovery owner. Recovery Role: " +
                $"{member.RecoveryRole}.");
        }

        if (member.Certificate != cmMember.SigningKey)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConsortiumManagerCertMismatch",
                $"Consortium Manager member certificate is not valid. Certificate: " +
                $"{member.Certificate}. Expected: {cmMember.SigningKey}.");
        }

        if (member.PublicEncryptionKey != cmMember.EncryptionKey)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConsortiumManagerEncryptionKeyMismatch",
                $"Consortium Manager encryption key is not valid. Encryption Key: " +
                $"{member.PublicEncryptionKey}. Expected: {cmMember.EncryptionKey}.");
        }
    }

    private static void ValidateOperatorMember(Member member)
    {
        if (member.PublicEncryptionKey != null)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "OperatorHasPublicEncryptionKey",
                $"Operator has public encryption key set. Recovery Role: " +
                $"{member.RecoveryRole}.");
        }
    }

    private static async Task ValidateRecoveryOperatorMember(
        Member member,
        RecoveryServiceReport recoveryServiceReport,
        IRecoveryServiceClient recoveryServiceClient)
    {
        if (member.MemberData.IsOperator)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConfidentialRecoveryMemberIsOperator",
                "Confidential Recovery member is marked as Operator.");
        }

        if (member.RecoveryRole != "Owner")
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConfidentialRecoveryMemberIsNotRecoveryOwner",
                $"Confidential Recovery member is not recovery owner. Recovery Role: " +
                $"{member.RecoveryRole}.");
        }

        if (member.MemberData.RecoveryServiceData?.HostData == null)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConfidentialRecoveryMemberHostDataNull",
                "Confidential Recovery member has null host data.");
        }

        if (member.MemberData.RecoveryServiceData.HostData != recoveryServiceReport.HostData)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConfidentialRecoveryMemberHostDataMismatch",
                $"Confidential Recovery member host data mismatches with that reported in " +
                $"Recovery Service report. " +
                $"Member host data: {member.MemberData.RecoveryServiceData.HostData}" +
                $"Recovery Service host data: {recoveryServiceReport.HostData}.");
        }

        var rsMember =
            await recoveryServiceClient.GetRecoveryServiceMember(member.MemberData.Identifier);
        if (member.Certificate.TrimEnd('\n') != rsMember.SigningCert)
        {
            throw new ApiException(
                HttpStatusCode.PreconditionFailed,
                "ConfidentialRecoveryMemberCertificateMismatch",
                $"Confidential Recovery member certificate mismatches with that reported by " +
                $"Recovery Service. " +
                $"Member certificate: {member.Certificate}" +
                $"Recovery Service certificate: {rsMember.SigningCert}.");
        }

        if (member.PublicEncryptionKey.TrimEnd('\n') != rsMember.EncryptionPublicKey)
        {
            throw new ApiException(
               HttpStatusCode.PreconditionFailed,
               "ConfidentialRecoveryMemberEncryptionPublicKeyMismatch",
               $"Confidential Recovery member encryption public key mismatches with that " +
               $"reported by Recovery Service. " +
               $"Member encryption public Key: {member.PublicEncryptionKey}" +
               $"Recovery Service encryption public key: {rsMember.EncryptionPublicKey}.");
        }

        if (member.MemberData.RecoveryServiceData.HostData !=
            rsMember.RecoveryServiceEnvInfo?.HostData)
        {
            throw new ApiException(
               HttpStatusCode.PreconditionFailed,
               "ConfidentialRecoveryMemberHostDataMismatch",
               $"Confidential Recovery member host data mismatches with that reported by " +
               $"Recovery Service. " +
               $"Member host data: {member.MemberData.RecoveryServiceData.HostData}" +
               $"Recovery Service host data: {rsMember.RecoveryServiceEnvInfo?.HostData}.");
        }
    }
}
