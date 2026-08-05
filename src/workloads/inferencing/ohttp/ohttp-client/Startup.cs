// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Controllers;

namespace OhttpClient;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(
            config,
            Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override bool EnableOpenTelemetry => true;

    public override void OnConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<KeyConfigCache>();
        services.AddHttpClient("OhttpGateway")
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                string? caPath = config["OHTTP_CA_CERT_PATH"];
                var handler = new HttpClientHandler();

                if (!string.IsNullOrEmpty(caPath) && File.Exists(caPath))
                {
                    var caCert =
                        X509CertificateLoader.LoadCertificateFromFile(caPath);
                    handler.ServerCertificateCustomValidationCallback =
                        (message, cert, chain, errors) =>
                    {
                        if (errors ==
                            System.Net.Security.SslPolicyErrors.None)
                        {
                            return true;
                        }

                        chain!.ChainPolicy.TrustMode =
                            X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(caCert);
                        return chain.Build(cert!);
                    };
                }

                return handler;
            });
    }
}
