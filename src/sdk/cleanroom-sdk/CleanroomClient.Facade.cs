// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Azure.Cleanroom.Governance.Client;

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// <see cref="ICleanroomClient"/> explicit implementations.
/// Clean signatures that auto-inject attestation credentials
/// and auto-decrypt responses.
/// </summary>
public partial class CleanroomClient
{
    private const string TransactionIdHeader =
        "x-ms-ccf-transaction-id";

    /// <inheritdoc/>
    async Task<GenerateEndorsedCertResponse>
        ICleanroomClient.GenerateEndorsedCertAsync(
            string contractId,
            string certData)
    {
        this.RequireAttestation();
        var dataBytes = Encoding.UTF8.GetBytes(certData);
        var request = new GenerateEndorsedCertRequest(
            this.attestation!.BuildEvidence(),
            this.attestation.BuildEncrypt(),
            this.attestation.BuildSign(dataBytes),
            Convert.ToBase64String(dataBytes));
        return await this.GenerateEndorsedCertAsync(
            contractId, request);
    }

    /// <inheritdoc/>
    async Task ICleanroomClient.SetDelegatePolicyAsync(
        string contractId,
        string delegateType,
        string delegateId,
        string policyData)
    {
        this.RequireAttestation();
        var dataBytes = Encoding.UTF8.GetBytes(policyData);
        var request = new SetDelegateCleanRoomPolicyRequest(
            this.attestation!.BuildEvidence(),
            this.attestation.BuildEncrypt(),
            this.attestation.BuildSign(dataBytes),
            Convert.ToBase64String(dataBytes));
        await this.SetDelegatePolicyAsync(
            contractId, delegateType, delegateId, request);
    }

    /// <inheritdoc/>
    async Task<GetSecretResponse> ICleanroomClient.GetSecretAsync(
        string contractId,
        string secretName)
    {
        this.RequireAttestation();
        var request = new GetSecretRequest(
            this.attestation!.BuildEvidence(),
            this.attestation.BuildEncrypt());
        var result = await this.generatedClient
            .GetSecretsClient()
            .GetSecretAsync(contractId, secretName, request);
        return result.Value;
    }

    /// <inheritdoc/>
    async Task<GetTokenResponse> ICleanroomClient.GetTokenAsync(
        string contractId)
    {
        this.RequireAttestation();
        var request = new GetTokenRequest(
            this.attestation!.BuildEvidence(),
            this.attestation.BuildEncrypt());
        var result = await this.generatedClient
            .GetTokenClient()
            .GetTokenAsync(contractId, request);
        return result.Value;
    }

    /// <inheritdoc/>
    async Task<GetAcceptedMemberDocumentResponse>
        ICleanroomClient.GetAcceptedMemberDocumentAsync(
            string contractId,
            string documentId)
    {
        this.RequireAttestation();
        var request = new GetAcceptedMemberDocumentRequest(
            this.attestation!.BuildEvidence(),
            this.attestation.BuildEncrypt());
        var result = await this.generatedClient
            .GetMemberDocumentsClient()
            .GetAcceptedMemberDocumentAsync(
                contractId, documentId, request);
        return result.Value;
    }

    /// <inheritdoc/>
    async Task<GetAcceptedUserDocumentResponse>
        ICleanroomClient.GetAcceptedUserDocumentAsync(
            string contractId,
            string documentId)
    {
        this.RequireAttestation();
        var request = new GetAcceptedUserDocumentRequest(
            this.attestation!.BuildEvidence(),
            this.attestation.BuildEncrypt());
        var result = await this.generatedClient
            .GetUserDocumentsClient()
            .GetAcceptedUserDocumentAsync(
                contractId, documentId, request);
        return result.Value;
    }

    /// <inheritdoc/>
    async Task ICleanroomClient.PutEventAsync(
        string contractId,
        string timestamp,
        string data,
        string? id,
        string? scope)
    {
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var base64Data = BinaryData.FromString(
            "\"" + Convert.ToBase64String(dataBytes) + "\"");

        ClientResult result;
        if (this.attestation != null &&
            this.attestation.PublicKeyPem != null)
        {
            var request = new PutEventRequest(
                timestamp,
                this.attestation.BuildEvidence(),
                this.attestation.BuildSign(dataBytes),
                base64Data);
            result = await this.generatedClient
                .GetEventsClient()
                .PutEventAsync(contractId, request, id, scope);
        }
        else
        {
            // JWT mode: use protocol method with simpler body.
            var request = new PutEventRequest(
                timestamp,
                new SnpEvidence(string.Empty, string.Empty, string.Empty),
                new Sign(string.Empty, string.Empty),
                base64Data);
            result = await this.generatedClient
                .GetEventsClient()
                .PutEventAsync(contractId, request, id, scope);
        }

        await this.WaitForTransactionCommitAsync(result);
    }

    private async Task WaitForTransactionCommitAsync(
        ClientResult result,
        TimeSpan? timeout = null)
    {
        var response = result.GetRawResponse();
        if (!response.Headers.TryGetValue(
            TransactionIdHeader, out string? transactionId))
        {
            return;
        }

        if (string.IsNullOrEmpty(transactionId))
        {
            return;
        }

        timeout ??= TimeSpan.FromSeconds(5);
        var endTime = DateTimeOffset.UtcNow + timeout.Value;
        var pipeline = this.generatedClient.Pipeline;
        var txUrl = $"app/tx?transaction_id={transactionId}";

        while (DateTimeOffset.UtcNow <= endTime)
        {
            var message = pipeline.CreateMessage();
            message.Request.Method = "GET";
            message.Request.Uri = new Uri(
                this.endpoint, txUrl);
            await pipeline.SendAsync(message);
            var content = message.Response!.Content!.ToString();
            using var body = JsonDocument.Parse(content);
            var status = body.RootElement
                .GetProperty("status").GetString()!;
            if (status == "Committed")
            {
                return;
            }

            if (status != "Unknown" && status != "Pending")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
