// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests;

/// <summary>
/// Test cases for network egress from the launched container and sidecars.
/// </summary>
[TestClass]
public class NetworkEgressTests : UnitTestBase
{
    private const string OutsideConnUri = "https://www.microsoft.com/";
    private const string LoopbackConnUri = "http://localhost:8290/";

    /// <summary>
    /// Test Network Connection from root test container to microsoft.com.
    /// </summary>
    /// <returns> Expectation: Allowed. </returns>
    [TestMethod]
    [TestCategory("network-egress")]
    public async Task TestNetworkEgressRun_RootContainer_OutsideCon()
    {
        HttpResponseMessage response = await this.HttpGet(OutsideConnUri).ConfigureAwait(true);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(
                "Internetbound URI :" +
                OutsideConnUri +
                " , cannot be reached from the test container.");
        }

        this.Logger.LogInformation(
            $"TestNetworkEgressRun_RootContainer_OutsideCon: " +
            $"ResponseCode {response.IsSuccessStatusCode}");
    }

    /// <summary>
    /// Test Network Connection from root test container to loopback interface.
    /// </summary>
    /// <returns> Expectation: Allowed.</returns>
    [TestMethod]
    [TestCategory("network-egress")]
    public async Task TestNetworkEgressRun_RootContainer_LoopbackCon()
    {
        HttpResponseMessage response = await this.HttpGet(LoopbackConnUri).ConfigureAwait(true);

        if (response.StatusCode != HttpStatusCode.NotFound &&
            !response.IsSuccessStatusCode)
        {
            throw new Exception(
                "Internal URI :" +
                LoopbackConnUri +
                " , cannot be reached from the test container.");
        }

        this.Logger.LogInformation(
            $"TestNetworkEgressRun_RootContainer_LoopbackCon: " +
            $"ResponseCode {response.IsSuccessStatusCode}");
    }

    /// <summary>
    /// Tests Network Connection from non-root test container to microsoft.com.
    /// </summary>
    /// <returns>BLOCKED (curl - denied). </returns>
    [TestMethod]
    [TestCategory("network-egress")]
    public async Task TestNetworkEgressRun_NonRootContainer_OutsideCon()
    {
        string aciContainerGroupId = this.Configuration["ACI_CONTAINER_GROUP_ID"]!;

        int exitCode = await Utils.FetchACIContainerExitCode(
            "curl-nonroot-network-egress",
            aciContainerGroupId,
            TimeSpan.FromMinutes(5),
            this.Logger);

        Assert.AreNotEqual(0, exitCode, $"Curl container exited with exitcode {exitCode}. " +
            $"launched container is able to communicate with internet");
    }

    /// <summary>
    /// Test Network Connection from non-root test container to loopback.
    /// </summary>
    /// <returns> Expectation: ALLOWED (curl - accepted). </returns>
    [TestMethod]
    [TestCategory("network-egress")]
    public async Task TestNetworkEgressRun_NonRootContainer_LoopbackCon()
    {
        string aciContainerGroupId = this.Configuration["ACI_CONTAINER_GROUP_ID"]!;

        int exitCode = await Utils.FetchACIContainerExitCode(
            "curl-nonroot-loopback-egress",
            aciContainerGroupId,
            TimeSpan.FromMinutes(5),
            this.Logger);

        Assert.AreEqual(0, exitCode, $"Curl container exited with exitcode {exitCode}. " +
            $"launched container is not able to communicate with loopback interfaces");
    }

    private async Task<HttpResponseMessage> HttpGet(string uri)
    {
        using HttpClient client = new();
        return await client.GetAsync(uri);
    }
}