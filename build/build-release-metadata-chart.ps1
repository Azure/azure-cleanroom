param(
    # Image tag: resolves image digests (Get-Digest) and stamps chart/artefact ':tag' refs.
    [parameter(Mandatory = $true)]
    [string]$tag,

    # Registry to resolve image digests from (the ACR just pushed to).
    [parameter(Mandatory = $true)]
    [string]$repo,

    # Registry the published image references point at (NOT where the chart is pushed).
    # Defaults to public MCR, the canonical registry external consumers pull from; onebox/CI
    # pass -publishRepo $repo since their images live in the local/ACR registry, not MCR.
    [string]$publishRepo = "mcr.microsoft.com/azurecleanroom",

    # Push the packaged chart to oci://<repo>/release-metadata (onebox/CI dogfood).
    [switch]$push,

    # Skip entries whose image isn't built/pushed (onebox/CI subset builds; release is strict).
    [switch]$skipMissing,

    # Include dev/test-only images that are built in onebox but never released to MCR (e.g. the
    # non-SNP local-skr stand-in used by the virtual provider). Off by default so the release
    # catalog stays limited to the shipped product surface.
    [switch]$includeDevImages,

    [string]$outDir = ""
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

. $buildRoot/helpers.ps1

# The catalog is a Helm chart, so its version must be SemVer. Derive it from -tag (real SemVer
# release tags pass through; onebox/CI tags like a run id become 1.0.42-v<trunc>). Consumers
# resolve the catalog by this same value (Get-SemanticVersionFromTag).
$releaseVersion = Get-SemanticVersionFromTag $tag

# Canonical release-metadata catalog (flat, kind-first). Every release is a full rebuild,
# so the whole catalog is resolved fresh at $tag (no carry-forward). Kind -> ref form:
#   images      -> <repo>/<path>@sha256:...   (digest-pinned)
#   helm-charts -> oci://<repo>/<path>:<tag>
#   artefacts   -> <repo>/<path>:<tag>        (policy docs, cgs-js-app, constitution, wheel)
# Logical names are unique within a kind (images and charts may reuse a name across kinds).
$catalog = [ordered]@{
    images        = [ordered]@{
        "ccf-run-js-app-virtual"            = "ccf/app/run-js/virtual"
        "ccf-run-js-app-snp"                = "ccf/app/run-js/snp"
        "ccf-recovery-agent"                = "ccf/ccf-recovery-agent"
        "ccf-recovery-service"              = "ccf/ccf-recovery-service"
        "ccf-consortium-manager"            = "ccf/ccf-consortium-manager"
        "cvm-attestation-verifier"          = "cvm/cvm-attestation-verifier"
        "ccr-proxy"                         = "ccr-proxy"
        "skr"                               = "skr"
        "cgs-client"                        = "cgs-client"
        "cgs-ui"                            = "cgs-ui"
        "ccr-init"                          = "ccr-init"
        "identity"                          = "identity"
        "blobfuse-launcher"                 = "blobfuse-launcher"
        "s3fs-launcher"                     = "s3fs-launcher"
        "code-launcher"                     = "code-launcher"
        "otel-collector"                    = "otel-collector"
        "ccr-secrets"                       = "ccr-secrets"
        "ccr-governance"                    = "ccr-governance"
        "cleanroom-client"                  = "cleanroom-client"
        "ccr-proxy-ext-processor"           = "ccr-proxy-ext-processor"
        "cvm-attestation-agent"             = "cvm/cvm-attestation-agent"
        "cleanroom-spark-analytics-agent"   = "workloads/cleanroom-spark-analytics-agent"
        "cleanroom-spark-frontend"          = "workloads/cleanroom-spark-frontend"
        "cleanroom-spark-analytics-app"     = "workloads/cleanroom-spark-analytics-app"
        "frontend-service"                  = "frontend-service"
        "ccf-provider-client"               = "ccf/ccf-provider-client"
        "cleanroom-cluster-provider-client" = "cleanroom-cluster/cleanroom-cluster-provider-client"
    }

    "helm-charts" = [ordered]@{
        "cleanroom-spark-analytics-agent" = "workloads/helm/cleanroom-spark-analytics-agent"
        "cleanroom-spark-frontend"        = "workloads/helm/cleanroom-spark-frontend"
        "frontend-service"                = "workloads/helm/frontend-service"
    }

    artefacts     = [ordered]@{
        "ccf-network-security-policy"                           = "policies/ccf/ccf-network-security-policy"
        "ccf-recovery-service-security-policy"                  = "policies/ccf/ccf-recovery-service-security-policy"
        "ccf-consortium-manager-security-policy"                = "policies/ccf/ccf-consortium-manager-security-policy"
        "ccf-consortium-manager-security-policy-vn2"            = "policies/ccf/ccf-consortium-manager-security-policy-vn2"
        "blobfuse-launcher-policy"                              = "policies/blobfuse-launcher-policy"
        "s3fs-launcher-policy"                                  = "policies/s3fs-launcher-policy"
        "ccr-governance-policy"                                 = "policies/ccr-governance-policy"
        "ccr-init-policy"                                       = "policies/ccr-init-policy"
        "ccr-secrets-policy"                                    = "policies/ccr-secrets-policy"
        "ccr-proxy-policy"                                      = "policies/ccr-proxy-policy"
        "ccr-proxy-ext-processor-policy"                        = "policies/ccr-proxy-ext-processor-policy"
        "code-launcher-policy"                                  = "policies/code-launcher-policy"
        "identity-policy"                                       = "policies/identity-policy"
        "otel-collector-policy"                                 = "policies/otel-collector-policy"
        "skr-policy"                                            = "policies/skr-policy"
        "ccr-governance-opa-policy"                             = "policies/ccr-governance-opa-policy"
        "cleanroom-spark-analytics-agent-security-policy"       = "policies/workloads/cleanroom-spark-analytics-agent-security-policy"
        "cleanroom-spark-frontend-security-policy"              = "policies/workloads/cleanroom-spark-frontend-security-policy"
        "cleanroom-spark-analytics-app-security-policy"         = "policies/workloads/cleanroom-spark-analytics-app-security-policy"
        "cleanroom-kserve-inferencing-agent-security-policy"    = "policies/workloads/cleanroom-kserve-inferencing-agent-security-policy"
        "cleanroom-kserve-inferencing-frontend-security-policy" = "policies/workloads/cleanroom-kserve-inferencing-frontend-security-policy"
        "frontend-service-security-policy"                      = "policies/frontend-service-security-policy"
        "cgs-js-app"                                            = "cgs-js-app"
        "cgs-constitution"                                      = "cgs-constitution"
        "cleanroom-cli-wheel"                                   = "cli/cleanroom-whl"
    }
}

# Dev/test-only images: built in onebox but never released to MCR, so they are added to the
# catalog only for onebox/CI (-includeDevImages) and kept out of the release catalog. local-skr
# is the non-SNP SKR stand-in the virtual provider pulls; resolving it from the catalog lets
# onebox drop its dedicated CCF_PROVIDER_LOCAL_SKR_IMAGE override.
if ($includeDevImages) {
    $catalog.images["local-skr"] = "local-skr"
}

# Resolves a single logical entry to its published reference string, based on kind.
function Resolve-Ref {
    param(
        [string]$kind,
        [string]$path,
        [string]$tag,
        [string]$repo,
        [string]$publishRepo
    )
    switch ($kind) {
        "images" {
            $digest = Get-Digest -repo $repo -containerName $path -tag $tag
            return "$publishRepo/$path@$digest"
        }
        "helm-charts" {
            return "oci://$publishRepo/${path}:$tag"
        }
        default {
            # artefacts (security-policy docs, js apps, constitutions, wheels)
            return "$publishRepo/${path}:$tag"
        }
    }
}

if ($outDir -eq "") {
    $outDir = "$root/.charts/release-metadata"
}
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force $outDir | Out-Null
}

# Full-rebuild releases: resolve every kind fresh at $tag (no carry-forward).
$resolved = [ordered]@{}
foreach ($kind in $catalog.Keys) {
    $kindOut = [ordered]@{}
    foreach ($name in $catalog[$kind].Keys) {
        $path = $catalog[$kind][$name]
        try {
            $kindOut[$name] =
                Resolve-Ref -kind $kind -path $path -tag $tag -repo $repo -publishRepo $publishRepo
        }
        catch {
            if ($skipMissing) {
                Write-Warning "Skipping '$name' ($path): not resolvable at ${repo}:$tag. $_"
            }
            else {
                throw
            }
        }
    }
    $resolved[$kind] = $kindOut
}

# Assemble the versioned metadata contract (metadata first, then the flat kind buckets).
$values = [ordered]@{
    apiVersion = "metadata.cleanroom.azure.com/v1"
    kind       = "ReleaseMetadata"
    metadata   = [ordered]@{
        release   = $releaseVersion
        published = (Get-Date -Format "yyyy-MM-dd")
    }
}
foreach ($kind in $resolved.Keys) {
    $values[$kind] = $resolved[$kind]
}

# Assemble a fresh chart directory and stamp the version (== the release tag).
$chartSrc = "$buildRoot/release-metadata-chart"
$chartDir = "$outDir/release-metadata"
if (Test-Path $chartDir) {
    Remove-Item -Recurse -Force $chartDir
}
Copy-Item -Recurse $chartSrc $chartDir

$chart = Get-Content -Path "$chartDir/Chart.yaml" -Raw | ConvertFrom-Yaml
$chart.version = $releaseVersion
($chart | ConvertTo-Yaml).TrimEnd() | Out-File "$chartDir/Chart.yaml"

# Generate values.yaml (the versioned metadata contract) from the resolved catalog.
$valuesPath = "$chartDir/values.yaml"
($values | ConvertTo-Yaml).TrimEnd() | Out-File $valuesPath

# Validate the generated contract against values.schema.json before publishing.
# helm enforces the schema on 'lint' (not on 'package'/'show values'); this is the
# producer-side guardrail for the non-deployable metadata chart.
Write-Host "Linting release-metadata chart against values.schema.json"
helm lint $chartDir

Write-Host "Packaging release-metadata chart version $releaseVersion"
helm package $chartDir --destination $outDir --version $releaseVersion

# Optionally publish the chart as an OCI artifact alongside the images. Onebox/CI
# points CCF_PROVIDER_RELEASE_METADATA_CHART_URL at oci://<publishRepo>/release-metadata
# and the provider resolves it with 'helm show values oci://...' -- no HTTP server.
if ($push) {
    Write-Host "Pushing release-metadata chart to oci://$repo"
    helm push "$outDir/release-metadata-$releaseVersion.tgz" "oci://$repo"
}
