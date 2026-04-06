// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;
using OpenTelemetry;

namespace Controllers;

internal class OpenTelemetryUtilities
{
    public static void SetLoggingContext(ActionExecutingContext actionContext)
    {
        Baggage.SetBaggage(
            BaggageItemName.CorrelationRequestId,
            GetClientRequestId(actionContext.HttpContext.Request));
        Baggage.SetBaggage(
            BaggageItemName.ClientRequestId,
            GetCorrelationRequestId(actionContext.HttpContext.Request));
    }

    private static string GetClientRequestId(HttpRequest request)
    {
        request.Headers.TryGetValue(
            CustomHttpHeader.MsClientRequestId,
            out StringValues clientRequestId);

        return clientRequestId.FirstOrDefault() ?? Guid.NewGuid().ToString();
    }

    private static string GetCorrelationRequestId(HttpRequest request)
    {
        request.Headers.TryGetValue(
            CustomHttpHeader.MsCorrelationRequestId,
            out StringValues correlationRequestId);

        if (!Guid.TryParse(correlationRequestId.FirstOrDefault(), out Guid correlationId))
        {
            correlationId = Guid.NewGuid();
        }

        return correlationId.ToString();
    }
}