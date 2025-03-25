// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfProvider;

public static class WorkspaceDirectories
{
    public static string GetNetworkDirectory(string networkName, InfraType infraType)
    {
        var infraTypeFolderName = infraType.ToString().ToLower();
        string wsDir =
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Directory.GetCurrentDirectory();
        return wsDir + $"/{infraTypeFolderName}/{networkName}";
    }

    public static string GetNodeDirectory(
        string nodeName,
        string networkName,
        InfraType infraType)
    {
        return GetNetworkDirectory(networkName, infraType) + $"/{nodeName}";
    }

    public static string GetConfigurationDirectory(
        string nodeName,
        string networkName,
        InfraType infraType)
    {
        return GetNodeDirectory(nodeName, networkName, infraType) + $"/config_data";
    }

    public static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");
        }

        DirectoryInfo[] dirs = dir.GetDirectories();
        Directory.CreateDirectory(destinationDir);

        foreach (FileInfo file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, overwrite: true);
        }

        if (recursive)
        {
            foreach (DirectoryInfo subDir in dirs)
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir, true);
            }
        }
    }
}
