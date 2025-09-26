// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Azure.Identity;
using Docker.DotNet;
using Microsoft.Identity.Client;
using Microsoft.Rest.TransientFaultHandling;

namespace Controllers;

public class ODataError
{
    public ODataError(string code, string message)
    {
        this.Error.Code = code;
        this.Error.Message = message;
    }

    public ErrorResponse Error { get; set; } = new();

    public static (int statuCode, ODataError error) FromException(Exception e)
    {
        int statusCode = 500;
        string code = e.GetType().Name;
        string message = e.Message;
        if (e is ApiException ae)
        {
            code = ae.Code;
            message = ae.Message;
            statusCode = (int)ae.StatusCode;
        }
        else if (e is Azure.RequestFailedException rfe)
        {
            code = rfe.ErrorCode ?? code;
            message = rfe.Message;
            statusCode = rfe.Status;
            if (statusCode == 200)
            {
#pragma warning disable MEN002 // Line is too long
                // This is because the Azure SDK is reporting the failure of the underlying
                // long-running operation, even though the HTTP response itself was 200.
                // Eg The SDK throws RequestFailedException when provisioning fails, even if every
                // individual HTTP call succeeded with 200. Eg:
                // {   "Error": {     "Code": "RequestFailedException",
                // "Message": "pulling image \u0022foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c\u0022;Successfully
                // pulled image \u0022foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c\u0022;
                // Started container;pulling image \u0022foo.azurecr.io/
                // ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c\u0022;Successfully pulled image \u0022foo.
                // azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c\u0022;Killing container ccr-envoy (platform initiated).;
                // Started container;pulling image \u0022foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c\u0022;
                // Failed to pull image \u0022foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c\u0022
                // : CriContainerActivator is getting initialized and not ready for use. Please see node health report for details.;
                // pulling image \u0022foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c\u0022;
                // Subscription deployment didn\u0027t reach a successful provisioning state after \u002700:30:00\u0027.\n
                // Status: 200 (OK)\n\nService request succeeded. Response content and headers are not included to avoid logging sensitive data.\n"   } }
                // Azure.RequestFailedException: pulling image "foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c";
                // Successfully pulled image "foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c";
                // Started container;pulling image "foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c";
                // Successfully pulled image "foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c";
                // Killing container ccr-envoy (platform initiated).;Started container;pulling image "foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c";
                // Failed to pull image "foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c":
                // CriContainerActivator is getting initialized and not ready for use. Please see node health report for details.;pulling image
                // "foo.azurecr.io/ccr-proxy@sha256:e044c9f83465203a9b5165155f99e238682b1ed6b54de165e2d15ab0882e434c";
                // Subscription deployment didn't reach a successful provisioning state after '00:30:00'.
                // Status: 200 (OK)  Service request succeeded. Response content and headers are not included to avoid logging sensitive data.
                // at Azure.Core.OperationInternal`1.GetResponseFromState(OperationState`1 state)
                // at Azure.Core.OperationInternal`1.UpdateStatusAsync(Boolean async, CancellationToken cancellationToken)
                // at Azure.Core.OperationInternalBase.UpdateStatusAsync(CancellationToken cancellationToken)
                // at Azure.Core.OperationPoller.WaitForCompletionAsync(Boolean async, OperationInternalBase operation, Nullable`1 delayHint, CancellationToken cancellationToken)
                // at Azure.Core.OperationInternalBase.WaitForCompletionResponseAsync(Boolean async, Nullable`1 pollingInterval, String scopeName, CancellationToken cancellationToken)
                // at Azure.Core.OperationInternal`1.WaitForCompletionAsync(Boolean async, Nullable`1 pollingInterval, CancellationToken cancellationToken)
                // at Azure.Core.OperationInternal`1.WaitForCompletionAsync(CancellationToken cancellationToken)
                // at Azure.ResourceManager.ContainerInstance.ContainerGroupCollection.CreateOrUpdateAsync(WaitUntil waitUntil, String containerGroupName, ContainerGroupData data, CancellationToken cancellationToken)
                // at AciLoadBalancer.AciEnvoyLoadBalancerProvider.CreateContainerGroup
#pragma warning restore MEN002 // Line is too long
                // Do not return a 200 success code to the clients of the API else they treat it as
                // success and move on.
                statusCode = (int)HttpStatusCode.FailedDependency;
                message = $"(Overriding return status of 200 to {statusCode} to force failure) "
                    + rfe.Message;
            }
        }
        else if (e is DockerApiException de)
        {
            statusCode = (int)de.StatusCode;
            code = de.StatusCode.ToString();
        }
        else if (e is HttpRequestWithStatusException se)
        {
            try
            {
                var o = JsonSerializer.Deserialize<ODataError>(se.Message);
                if (o != null && !string.IsNullOrEmpty(o.Error?.Code))
                {
                    code = o.Error.Code;
                    message = o.Error.Message;
                }
                else
                {
                    code = se.StatusCode.ToString();
                    message = se.Message;
                }
            }
            catch
            {
                code = se.StatusCode.ToString();
                message = se.Message;
            }

            statusCode = (int)se.StatusCode;
        }
        else if (e is AuthenticationFailedException afe &&
            afe.InnerException is MsalException mse)
        {
            message += $" {mse.Message}";
        }

        var error = new ODataError(code, message);
        return (statusCode, error);
    }

    public class ErrorResponse
    {
        public string Code { get; set; } = default!;

        public string Message { get; set; } = default!;
    }
}
