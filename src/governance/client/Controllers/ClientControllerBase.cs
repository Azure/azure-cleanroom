// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public abstract class ClientControllerBase : ControllerBase
{
    private readonly IHttpContextAccessor httpContextAccessor;

    public ClientControllerBase(
        ILogger logger,
        IHttpContextAccessor httpContextAccessor)
    {
        this.Logger = logger;
        this.httpContextAccessor = httpContextAccessor;

        string? ccfEndpoint = this.GetHeader("x-ms-ccf-endpoint");
        string? serviceCertPem = this.GetServiceCertPem("x-ms-service-cert");
        CcfServiceCertLocator? certLocator =
            this.GetServiceCertLocator("x-ms-service-cert-discovery");

        this.CcfClientManager = new CcfClientManager(
            this.Logger,
            ccfEndpoint,
            serviceCertPem,
            certLocator);
    }

    protected ILogger Logger { get; }

    protected CcfClientManager CcfClientManager { get; }

    protected string? GetHeader(string header)
    {
        if (this.httpContextAccessor.HttpContext != null &&
            this.httpContextAccessor.HttpContext.Request.Headers.TryGetValue(
                header,
                out var value))
        {
            return value.ToString();
        }

        return null;
    }

    protected string? GetServiceCertPem(string serviceCertHeader)
    {
        string? serviceCertBase64 = this.GetHeader(serviceCertHeader);
        if (serviceCertBase64 != null)
        {
            byte[] bytes = Convert.FromBase64String(serviceCertBase64);
            return Encoding.UTF8.GetString(bytes);
        }

        return null;
    }

    protected CcfServiceCertLocator? GetServiceCertLocator(string serviceCertDiscoveryHeader)
    {
        string? serviceCertDiscoveryBase64 = this.GetHeader(serviceCertDiscoveryHeader);
        if (serviceCertDiscoveryBase64 != null)
        {
            byte[] bytes = Convert.FromBase64String(serviceCertDiscoveryBase64);
            var model = JsonSerializer.Deserialize<CcfServiceCertDiscoveryModel>(
                Encoding.UTF8.GetString(bytes))!;
            return new CcfServiceCertLocator(this.Logger, model);
        }

        return null;
    }
}
