param(
    [parameter(Mandatory = $true)]
    [string]$tag,

    [parameter(Mandatory = $true)]
    [string]$repo,

    [string]$outDir = "",

    [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

if ($outDir -eq "") {
    $outDir = "."
}

# Runtime images for model serving. KServe runtimes are on docker.io,
# llama.cpp server is on ghcr.io. Each runtime has its own version tag.
$runtimes = @(
    [ordered]@{
        runtime  = "kserve-sklearnserver"
        registry = "docker.io"
        image    = "kserve/sklearnserver"
        tag      = "v0.17.0"
    }
    [ordered]@{
        runtime  = "llamacpp-server"
        registry = "ghcr.io"
        image    = "ggml-org/llama.cpp"
        tag      = "server"
    }
)

# Resolve digests from the source registry at build time.
$digests = @()
foreach ($rt in $runtimes) {
    $digest = Get-Digest -repo $rt.registry -containerName $rt.image -tag $rt.tag
    Write-Host "Resolved $($rt.registry)/$($rt.image):$($rt.tag) -> $digest"
    $digests += [ordered]@{
        runtime = $rt.runtime
        image   = "$($rt.registry)/$($rt.image)"
        digest  = $digest
    }
}

$digests | ConvertTo-Yaml | Out-File $outDir/inf-runtime-digests.yaml

if ($push) {
    Set-Location $outDir
    oras push "$repo/inf-runtime-digests:$tag" ./inf-runtime-digests.yaml
}
