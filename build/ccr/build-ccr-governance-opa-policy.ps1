param(
    $repo,

    $tag = "latest",

    [switch]$push = $false,

    $outputPath = ""
)

$ErrorActionPreference = "Stop"
$root = git rev-parse --show-toplevel
$policyFilesPath = "$root/src/proxy/policies"

if ($outputPath -eq "") {
    $outputPath = [IO.Path]::GetTempPath() + "ccr-governance-opa-policy"
}

function cleanup() {
    if (test-path $outputPath\ccr-governance-opa-policy.tar.gz) {
        Remove-Item -Force $outputPath\ccr-governance-opa-policy.tar.gz
    }
}

cleanup

mkdir -p $outputPath

# Create the bundle.
# https://www.openpolicyagent.org/docs/latest/management-bundles/#building-and-publishing-policy-containers
$uid = id -u ${env:USER}
$gid = id -g ${env:USER}

$opaImage = "openpolicyagent/opa:0.69.0"
if ($env:GITHUB_ACTIONS -eq "true") {
    $opaImage = "cleanroombuild.azurecr.io/openpolicyagent/opa:0.69.0"
}

docker run --rm `
    -u ${uid}:${gid} `
    -v ${policyFilesPath}:/workspace `
    -v ${outputPath}:/output `
    -w /workspace `
    $opaImage build . --bundle -o /output/ccr-governance-opa-policy.tar.gz

if ($push) {
    # Push the bundle to the registry. Need to Set-Location as need to use "./governance-policy.tar.gz"
    # as the path in the orash push command. If giving a path like /some/dir/governance-policy.tar.gz
    # then oras pull fails with "Error: failed to resolve path for writing: path traversal disallowed"
    Push-Location
    Set-Location $outputPath
    oras push $repo/policies/ccr-governance-opa-policy:$tag `
        --config $policyFilesPath/config.json:application/vnd.oci.image.config.v1+json `
        ./ccr-governance-opa-policy.tar.gz:application/vnd.oci.image.layer.v1.tar+gzip
    Pop-Location
}

cleanup
