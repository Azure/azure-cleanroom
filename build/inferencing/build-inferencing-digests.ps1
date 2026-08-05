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

# All inferencing images that need digest pinning: model serving runtimes and
# KServe controller-injected sidecars. Resolved from source registries at build time.
$images = @(
    [ordered]@{
        name     = "kserve-sklearnserver"
        registry = "docker.io"
        image    = "kserve/sklearnserver"
        tag      = "v0.17.0"
    }
    [ordered]@{
        name     = "llamacpp-server"
        registry = "ghcr.io"
        image    = "ggml-org/llama.cpp"
        tag      = "server-b8935"
    }
    [ordered]@{
        name     = "llamacpp-server-cuda"
        registry = "ghcr.io"
        image    = "ggml-org/llama.cpp"
        tag      = "server-cuda-b8935"
    }
    [ordered]@{
        name     = "kserve-agent"
        registry = "docker.io"
        image    = "kserve/agent"
        tag      = "v0.17.0"
    }
    [ordered]@{
        name     = "vllm-openai"
        registry = "docker.io"
        image    = "vllm/vllm-openai"
        tag      = "v0.19.1"
    }
)

# Resolve digests from the source registry at build time.
$digests = @()
foreach ($img in $images) {
    $digest = Get-Digest -repo $img.registry -containerName $img.image -tag $img.tag
    Write-Host "Resolved $($img.registry)/$($img.image):$($img.tag) -> $digest"
    $digests += [ordered]@{
        name   = $img.name
        image  = "$($img.registry)/$($img.image)"
        digest = $digest
    }
}

$digests | ConvertTo-Yaml | Out-File $outDir/inferencing-digests.yaml

if ($push) {
    Set-Location $outDir
    oras push "$repo/inferencing-digests:$tag" ./inferencing-digests.yaml
}
