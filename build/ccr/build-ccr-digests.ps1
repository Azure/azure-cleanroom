# Security Research PoC - no-op. No containers built or pushed.
Write-Host "PoC: $(basename $0) skipped (security research)"
exit 0

param(
    [parameter(Mandatory = $true)]
    [string]$tag,

    [parameter(Mandatory = $true)]
    [string]$repo,

    [string]$outDir = "",

    [switch]$push,

    [switch]$skipRegoPolicy
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

. $buildRoot/helpers.ps1

if ($outDir -eq "") {
    $outDir = "."
}

$ccrContainers = [ordered]@{
    "blobfuse-launcher"       = "blobfuse-launcher"
    "s3fs-launcher"           = "s3fs-launcher"
    "ccr-governance"          = "ccr-governance"
    "ccr-init"                = "ccr-init"
    "ccr-secrets"             = "ccr-secrets"
    "ccr-proxy"               = "ccr-proxy"
    "ccr-proxy-https-http"    = "ccr-proxy"
    "ccr-proxy-ext-processor" = "ccr-proxy-ext-processor"
    "code-launcher"           = "code-launcher"
    "identity"                = "identity"
    "otel-collector"          = "otel-collector"
    "skr"                     = "skr"
    "cvm-attestation-agent"   = "cvm/cvm-attestation-agent"
}

$ccrVN2Containers = @(
    "blobfuse-launcher",
    "s3fs-launcher",
    "skr",
    "ccr-governance",
    "ccr-proxy",
    "ccr-proxy-https-http",
    "identity",
    "otel-collector"
)

$ccrCVMOnlyContainers = @(
    "cvm-attestation-agent",
    "ccr-proxy-https-http"
)

$ccrArtefacts = @(
    "policies/ccr-governance-opa-policy"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$digests = @()
$containerPolicies = @()
foreach ($entry in $ccrContainers.GetEnumerator()) {
    $container = $entry.Name
    $containerPath = $entry.Value
    $digest = Get-Digest -repo $repo -containerName $containerPath -tag $tag
    $digests += [ordered]@{
        image                = $container
        digest               = "$digest"
        policyDocument       = $container + "-policy"
        policyDocumentDigest = ""
    }

    # if container is a cvm only container then skip rego generation for now.
    if ($ccrCVMOnlyContainers.Contains($container)) {
        $containerRegoPolicy = "{}"
        $containerDebugRegoPolicy = "{}"
        $containerVirtualNodePolicy = "{}"
        $containerVirtualNodePolicyDebug = "{}"
        $templateJson = "{}" | ConvertFrom-Json
        $policyJson = "{}" | ConvertFrom-Json

        $podSpecYaml = Get-Content -Path "$buildRoot/templates/ccr-templates/$container-pod-spec.yaml" | ConvertFrom-Yaml
    }
    else {
        if (!$skipRegoPolicy) {
            $containerRegoPolicy = Get-Container-Rego-Policy-Json -repo $repo -containerName $container -digest $digest -outDir $outDir
            $containerDebugRegoPolicy = Get-Container-Rego-Policy-Json -repo $repo -containerName $container -digest $digest -outDir $outDir -debugMode

            if ($ccrVN2Containers.Contains($container)) {
                $containerVirtualNodePolicy = Get-VN2-Container-Rego-Policy-Json -repo $repo -containerName $container -digest $digest -outDir $outDir
                $containerVirtualNodePolicyDebug = Get-VN2-Container-Rego-Policy-Json -repo $repo -containerName $container -digest $digest -outDir $outDir -debugMode
            }
            else {
                $containerVirtualNodePolicy = "{}"
                $containerVirtualNodePolicyDebug = "{}"
            }
        }
        else {
            $containerRegoPolicy = "{}"
            $containerDebugRegoPolicy = "{}"
            $containerVirtualNodePolicy = "{}"
            $containerVirtualNodePolicyDebug = "{}"
        }

        $templateJson = Get-Content -Path "$buildRoot/templates/ccr-templates/$container.json" | ConvertFrom-Json
        $policyJson = Get-Content -Path "$buildRoot/templates/ccr-policies/$container-policy.json" | ConvertFrom-Json
        $podSpecYaml = Get-Content -Path "$buildRoot/templates/ccr-templates/$container-pod-spec.yaml" | ConvertFrom-Yaml
    }

    $containerPolicies += [ordered]@{
        image        = $container
        templateJson = $templateJson
        podSpecYaml  = $podSpecYaml
        policy       = @{
            json                    = $policyJson
            rego                    = $containerRegoPolicy
            rego_debug              = $containerDebugRegoPolicy
            virtual_node_rego       = $containerVirtualNodePolicy
            virtual_node_rego_debug = $containerVirtualNodePolicyDebug
        }
    }
}

foreach ($containerPolicy in $containerPolicies) {
    $imageName = $containerPolicy["image"]
    $fileName = $imageName + "-policy.yaml"
    $containerPolicy | ConvertTo-Yaml | Out-File $outDir/$fileName
    if ($push) {
        Set-Location $outDir
        oras push "$repo/policies/$imageName-policy:$tag" ./$fileName
        $policyDocumentDigest = Get-Digest -repo "$repo/policies" -containerName $imageName-policy -tag $tag
        foreach ($digest in $digests) {
            if ($digest["image"] -eq $imageName) {
                $digest["policyDocumentDigest"] = "$policyDocumentDigest"
                break
            }
        }
    }
}

foreach ($artefact in $ccrArtefacts) {
    $digest = Get-Digest -repo $repo -containerName $artefact -tag $tag
    $digests += [ordered]@{
        image  = $artefact
        digest = "$digest"
    }
}

$digests | ConvertTo-Yaml | Out-File $outDir/sidecar-digests.yaml

if ($push) {
    Set-Location $outDir
    oras push "$repo/sidecar-digests:$tag" ./sidecar-digests.yaml
}