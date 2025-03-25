[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]
    $tag,

    [Parameter(Mandatory = $true)]
    [string]
    $releaseType,

    [Parameter(Mandatory = $true)]
    [string]
    $environment
)

function Verify-Attestation {
    param (
        [string]
        $tag,

        [string]
        $container,

        [string]
        $environment
    )

    $repo = "mcr.microsoft.com/azurecleanroom"
    if ($environment -eq "internal") {
        az acr login -n cleanroomsidecars
        $repo = "cleanroomsidecars.azurecr.io/internal/azurecleanroom"
    }

    $timeout = New-TimeSpan -Minutes 15
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    while ($stopwatch.Elapsed -lt $timeout) {
        gh attestation verify `
            "oci://$repo/$($container):$tag" `
            --repo $env:GH_REPOSITORY --format json

        if ($LASTEXITCODE -eq 0) {
            $stopwatch.Stop()
            return
        }

        Write-Host "Attestation verification failed for $container. Retrying in 10 seconds..."
        Start-Sleep -Seconds 10
    }

    $stopwatch.Stop()
    throw "Attestation verification failed for $container"

}

$ccrContainers = @(
    "ccr-init",
    "identity"
    "blobfuse-launcher",
    "code-launcher",
    "otel-collector",
    "ccr-attestation",
    "ccr-secrets",
    "ccr-governance",
    "cleanroom-client",
    "ccr-proxy",
    "ccr-proxy-ext-processor"
)

$ccrVersionsDocuments = "versions/ccr-containers"

$sidecarDigestsDocument = "sidecar-digests"

$ccrPolicyDocuments = @(
    "policies/ccr-attestation-policy",
    "policies/ccr-governance-policy",
    "policies/ccr-init-policy",
    "policies/ccr-proxy-ext-processor-policy",
    "policies/ccr-proxy-policy",
    "policies/ccr-secrets-policy",
    "policies/code-launcher-policy",
    "policies/identity-policy",
    "policies/otel-collector-policy",
    "policies/blobfuse-launcher-policy",
    "policies/skr-policy"
)

$govClientContainers = @(
    "cgs-client",
    "cgs-ui"
)

$govClientVersionsDocuments = @(
    "versions/cgs-client",
    "versions/cgs-ui"
)

$cgsConstitution = "cgs-constitution"
$cgsConstitutionVersionDocument = "versions/cgs-constitution"

$cgsJsApp = "cgs-js-app"
$cgsJsAppVersionDocument = "versions/cgs-js-app"

$ccfProviderClient = "ccf/ccf-provider-client"

$ccfNginx = "ccf/ccf-nginx"

$ccfProviders = @(
    "ccf/app/run-js/snp",
    "ccf/app/run-js/virtual"
)

$ccfRecoveryService = "ccf/ccf-recovery-service"
$ccfRecoveryAgent = "ccf/ccf-recovery-agent"

if ($releaseType.Contains("cleanroom-containers")) {
    foreach ($container in $ccrContainers) {
        Verify-Attestation -tag $tag -container $container -environment $environment
    }

    Verify-Attestation -tag $tag -container $sidecarDigestsDocument -environment $environment
    Verify-Attestation -tag "latest" -container $ccrVersionsDocuments -environment $environment

    foreach ($document in $ccrPolicyDocuments) {
        Verify-Attestation -tag $tag -container $document -environment $environment
    }
}

if ($releaseType.Contains("governance-client-containers")) {
    foreach ($container in $govClientContainers) {
        Verify-Attestation -tag $tag -container $container -environment $environment
    }

    foreach ($document in $govClientVersionsDocuments) {
        Verify-Attestation -tag "latest" -container $document -environment $environment
    }
}

if ($releaseType.Contains(("cgs-constitution"))) {
    Verify-Attestation -tag $tag -container $cgsConstitution -environment $environment
    Verify-Attestation -tag "latest" -container $cgsConstitutionVersionDocument -environment $environment
}

if ($releaseType.Contains(("cgs-js-app"))) {
    Verify-Attestation -tag $tag -container $cgsJsApp -environment $environment
    Verify-Attestation -tag "latest" -container $cgsJsAppVersionDocument -environment $environment
}

if ($releaseType.Contains(("ccf-provider-client"))) {
    Verify-Attestation -tag $tag -container $ccfProviderClient -environment $environment
}

if ($releaseType.Contains(("ccf-nginx"))) {
    Verify-Attestation -tag $tag -container $ccfNginx -environment $environment
}

if ($releaseType.Contains(("ccf-providers"))) {
    foreach ($container in $ccfProviders) {
        Verify-Attestation -tag $tag -container $container -environment $environment
    }
}

if ($releaseType.Contains(("ccf-recovery-service"))) {
    Verify-Attestation -tag $tag -container $ccfRecoveryService -environment $environment
}

if ($releaseType.Contains(("ccf-recovery-agent"))) {
    Verify-Attestation -tag $tag -container $ccfRecoveryAgent -environment $environment
}

Verify-Attestation -tag $tag -container cli/cleanroom-whl -environment $environment