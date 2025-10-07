// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Controllers;

namespace IdentitySidecar;

public class Program
{
    public static void Main(string[] args)
    {
        ApiMain.Main(
            args,
            builder => new Startup(builder.Configuration),
            (configBuilder) =>
            {
                string identityConfig = Environment.GetEnvironmentVariable("IdentitySideCarArgs")!;
                if (!string.IsNullOrWhiteSpace(identityConfig))
                {
                    var base64EncodedBytes = Convert.FromBase64String(identityConfig);
                    var identityConfigStr = Encoding.UTF8.GetString(base64EncodedBytes);
                    configBuilder.AddJsonStream(
                        new MemoryStream(Encoding.UTF8.GetBytes(identityConfigStr)));
                }
            });
    }
}
