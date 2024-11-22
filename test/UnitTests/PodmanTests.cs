// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Ignore Spelling: Podman
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests;

[TestClass]
public class PodmanTests : UnitTestBase
{
    [TestMethod]
    [TestCategory("podman")]
    public async Task TestPodmanRun_WaitForFile_Success()
    {
        string id = this.Configuration["ACI_CONTAINER_GROUP_ID"]!;
        int expectedExitCode = 0;

        int exitCode = await Utils.FetchACIContainerExitCode(
            "wait-for-file-container",
            id,
            TimeSpan.FromMinutes(5),
            this.Logger);

        Assert.AreEqual(
            exitCode,
            expectedExitCode,
            "TestPodmanRun_WaitForFile_Success: " +
            $"wait for file container exited with exitcode {exitCode}");
    }

    [TestMethod]
    [TestCategory("podman")]
    public async Task TestPodmanRun_WaitForFile_Failure()
    {
        string id = this.Configuration["ACI_CONTAINER_GROUP_ID"]!;
        int expectedExitCode = 143;

        int exitCode = await Utils.FetchACIContainerExitCode(
            "wait-for-file-container-failed",
            id,
            TimeSpan.FromMinutes(5),
            this.Logger);

        Assert.AreEqual(
            exitCode,
            expectedExitCode,
            "TestPodmanRun_WaitForFile_Failure:" +
            $"wait for file container exited with exitcode {exitCode}");
    }
}