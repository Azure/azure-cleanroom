// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace UnitTests;

/// <summary>
/// The test setting names.
/// </summary>
public static class TestSettingName
{
    /// <summary>
    /// The Azure Key Vault endpoint.
    /// </summary>
    public const string AkvEndpoint = "AKV_ENDPOINT";

    /// <summary>
    /// The MAA endpoint.
    /// </summary>
    public const string MaaEndpoint = "MAA_ENDPOINT";

    /// <summary>
    /// The key identifier.
    /// </summary>
    public const string Kid = "KID";

    /// <summary>
    /// The port at which the secure key release container is exposed.
    /// </summary>
    public const string SkrPort = "SKR_PORT";

    /// <summary>
    /// The port at which the identity container is exposed.
    /// </summary>
    public const string IdentityPort = "IDENTITY_PORT";

    /// <summary>
    /// The tenant ID.
    /// </summary>
    public const string TenantId = "TENANT_ID";

    /// <summary>
    /// The client ID to be used for access.
    /// </summary>
    public const string ClientId = "CLIENT_ID";

    /// <summary>
    /// The details related to secret based authentication.
    /// </summary>
    public const string SecretAuthentication = "SecretAuthentication";

    /// <summary>
    /// The details related to certificate based authentication.
    /// </summary>
    public const string CertificateAuthentication = "CertificateAuthentication";
}