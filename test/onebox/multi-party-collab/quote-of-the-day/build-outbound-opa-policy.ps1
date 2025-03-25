param(
    $repo,

    $tag = "latest",

    $name = "quote-of-the-day-policy",

    [switch]$push = $false
)

$ErrorActionPreference = "Stop"
$policyFilesPath = "$PSScriptRoot/publisher/policy"
$outputPath = [IO.Path]::GetTempPath() + "$name"

function cleanup() {
    if (test-path $outputPath\policy\$name.tar.gz) {
        Remove-Item -Force $outputPath\policy\$name.tar.gz
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

mkdir -p $outputPath/policy

docker run --rm `
    -u ${uid}:${gid} `
    -v ${policyFilesPath}:/workspace `
    -v ${outputPath}/policy:/output `
    -w /workspace `
    $opaImage build . --bundle -o /output/$name.tar.gz

if ($push) {
    # Push the bundle to the registry. Need to Set-Location as need to use "./governance-policy.tar.gz"
    # as the path in the orash push command. If giving a path like /some/dir/governance-policy.tar.gz
    # then oras pull fails with "Error: failed to resolve path for writing: path traversal disallowed"
    Push-Location
    Set-Location $outputPath/policy
    oras push $repo/${name}:$tag `
        --config $policyFilesPath/config.json:application/vnd.oci.image.config.v1+json `
        ./$name.tar.gz:application/vnd.oci.image.layer.v1.tar+gzip
    Pop-Location
}