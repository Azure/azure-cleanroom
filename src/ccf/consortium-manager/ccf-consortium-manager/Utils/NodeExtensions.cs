// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AttestationClient;
using CcfConsortiumMgr.Clients.Node.Models;

namespace CcfConsortiumMgr.Utils;

public static class NodeExtensions
{
    private const string AllowAllHostData =
        "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20";

    public static string GetSnpHostData(this QuotesList quotes)
    {
        NodeQuote quote = quotes.Quotes[0];

        if (quote.Format == "Insecure_Virtual")
        {
            return AllowAllHostData;
        }
        else
        {
            var report = SnpReport.VerifySnpAttestation(
                quote.Raw,
                quote.Endorsements,
                uvmEndorsements: null);
            return report.HostData.ToLower();
        }
    }
}
