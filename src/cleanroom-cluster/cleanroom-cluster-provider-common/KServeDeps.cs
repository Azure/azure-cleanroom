// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

/// <summary>
/// Reads KServe dependency versions from the kserve-deps.env file baked into
/// the container image at build time.
/// </summary>
public class KServeDeps
{
    // KServe version is the only hardcoded constant. All other dependency
    // versions are derived from the kserve-deps.env file for this version.
    public const string KServeVersion = "v0.17.0";

    // Path to the deps file baked into the Docker image.
    private const string DepsFilePath = "/app/kserve-deps.env";

    private static readonly Lock CacheLock = new();
    private static Dictionary<string, string>? cachedDeps;

    /// <summary>
    /// Reads and caches the kserve-deps.env key-value pairs.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <returns>Parsed key-value pairs from kserve-deps.env.</returns>
    public static Dictionary<string, string> GetDeps(ILogger logger)
    {
        if (cachedDeps != null)
        {
            return cachedDeps;
        }

        lock (CacheLock)
        {
            if (cachedDeps != null)
            {
                return cachedDeps;
            }

            cachedDeps = LoadDeps(logger);
            return cachedDeps;
        }
    }

    /// <summary>
    /// Gets the cert-manager version from kserve-deps.env.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <returns>The cert-manager version string.</returns>
    public static string GetCertManagerVersion(ILogger logger)
    {
        var deps = GetDeps(logger);
        return deps["CERT_MANAGER_VERSION"];
    }

    private static Dictionary<string, string> LoadDeps(ILogger logger)
    {
        logger.LogInformation(
            $"Loading KServe dependency versions from: {DepsFilePath}");

        string content = File.ReadAllText(DepsFilePath);
        var deps = new Dictionary<string, string>();
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                trimmed.StartsWith('#'))
            {
                continue;
            }

            int eqIndex = trimmed.IndexOf('=');
            if (eqIndex > 0)
            {
                string key = trimmed[..eqIndex].Trim();
                string value = trimmed[(eqIndex + 1)..].Trim();
                deps[key] = value;
            }
        }

        logger.LogInformation(
            $"Resolved KServe {KServeVersion} dependencies: " +
            $"cert-manager={deps["CERT_MANAGER_VERSION"]}.");

        return deps;
    }
}
