// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests;

/// <summary>
/// Tests for secure key release.
/// </summary>
[TestClass]
public class SecureKeyReleaseTests : UnitTestBase
{
    /// <summary>
    /// Test secure key release.
    /// </summary>
    /// <returns>A task representing the operation.</returns>
    [TestMethod]
    [TestCategory("key_release")]
    public async Task TestSecureKeyReleaseAsync()
    {
        string akvEndpoint = this.Configuration[TestSettingName.AkvEndpoint]!;
        string maaEndpoint = this.Configuration[TestSettingName.MaaEndpoint]!;
        string kid = this.Configuration[TestSettingName.Kid]!;
        string skrPort = this.Configuration[TestSettingName.SkrPort]!;

        var payload = new SecureKeyReleasePayload
        {
            MaaEndpoint = maaEndpoint,
            AkvEndpoint = akvEndpoint,
            KID = kid,
        };

        await Utils.SecureKeyReleaseAsync(skrPort, payload, this.Logger);
    }
}