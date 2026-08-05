[CmdletBinding()]
param(
    [string]$repo = "localhost:5000",
    [string]$tag = "latest",
    [ValidateSet("onebox", "cvm")]
    [string]$mode = "onebox",
    [string]$cgsEndpoint = "",
    [string]$contractId = "",
    [string]$serviceCertBase64 = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($cgsEndpoint -eq "") {
    throw "deploy-csi-daemonset.ps1: -cgsEndpoint is required (used by ccr-governance container)"
}
if ($serviceCertBase64 -eq "") {
    throw "deploy-csi-daemonset.ps1: -serviceCertBase64 is required (used by ccr-governance container)"
}

if ($contractId -eq "") {
    # Per-request contract routing is preferred. This value is now only a fallback
    # used by ccr-governance when a request does not send an override header.
    $contractId = "placeholder"
    Write-Warning "deploy-csi-daemonset.ps1: -contractId not specified; using fallback '$contractId'."
}

$root = git rev-parse --show-toplevel
$deployDir = "$root/poc/csi-driver/deploy"

Write-Host "Deploying CSI driver infrastructure (mode=$mode)..."

# Apply CSIDriver resource and RBAC.
kubectl apply -f $deployDir/csidriver.yaml
kubectl apply -f $deployDir/rbac.yaml

# Select images based on mode.
if ($mode -eq "cvm") {
    $governanceImage = "$repo/ccr-governance:$tag"
    $skrImage = "$repo/skr:$tag"
} else {
    $governanceImage = "$repo/ccr-governance-virtual:$tag"
    $skrImage = "$repo/local-skr:$tag"
}

# Generate the DaemonSet YAML with the correct image references.
$daemonsetTemplate = Get-Content "$deployDir/daemonset-template.yaml" -Raw
$daemonsetTemplate = $daemonsetTemplate -replace '\$\{REPO\}', $repo
$daemonsetTemplate = $daemonsetTemplate -replace '\$\{TAG\}', $tag
$daemonsetTemplate = $daemonsetTemplate -replace '\$\{GOVERNANCE_IMAGE\}', $governanceImage
$daemonsetTemplate = $daemonsetTemplate -replace '\$\{SKR_IMAGE\}', $skrImage
$daemonsetTemplate = $daemonsetTemplate -replace '\$\{CGS_ENDPOINT\}', $cgsEndpoint
$daemonsetTemplate = $daemonsetTemplate -replace '\$\{CONTRACT_ID\}', $contractId
$daemonsetTemplate = $daemonsetTemplate -replace '\$\{SERVICE_CERT_BASE64\}', $serviceCertBase64

$outFile = "$deployDir/onebox/daemonset-generated.yaml"
$daemonsetTemplate | Set-Content $outFile

kubectl apply -f $outFile

Write-Host "Waiting for CSI DaemonSet to be ready..."
kubectl rollout status daemonset/cleanroom-csi-driver -n kube-system --timeout=120s

Write-Host "CSI driver DaemonSet deployed successfully."
