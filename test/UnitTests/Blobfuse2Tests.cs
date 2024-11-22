// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests;

/// <summary>
/// Tests for blobfuse2 functionality.
/// </summary>
[TestClass]
public class Blobfuse2Tests : UnitTestBase
{
    /// <summary>
    /// Test blobfuse2.
    /// </summary>
    /// <returns>A task representing the operation.</returns>
    [TestMethod]
    [TestCategory("blobfuse2")]
    public async Task TestMountPointAvailableAsync()
    {
        string? expectedFilePath = this.Configuration["STORAGE_CONTAINER_TEST_FILE_PATH"];
        int maxWaitLoop = 30;
        int loopCount = 0;
        while (!System.IO.File.Exists(expectedFilePath))
        {
            if (loopCount > maxWaitLoop)
            {
                this.Logger.LogError($"Did not find file: {expectedFilePath}.");
                throw new Exception($"Did not find file: {expectedFilePath}");
            }

            this.Logger.LogInformation($"Waiting for file: {expectedFilePath}.");
            await Task.Delay(TimeSpan.FromSeconds(1));
            loopCount++;
        }

        string content = await System.IO.File.ReadAllTextAsync(expectedFilePath);
        this.Logger.LogInformation($"File {expectedFilePath} was found with content: {content}");
    }
}