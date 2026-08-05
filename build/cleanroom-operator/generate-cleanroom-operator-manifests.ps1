<#
.SYNOPSIS
    Regenerates CRD YAML, RBAC ClusterRole, and deepcopy from Go types
    using controller-gen.

.DESCRIPTION
    Run this script after modifying api/v1alpha1/*_types.go or
    internal/controller/*_controller.go to update:
    - helm/chart/templates/crds/       (CRD YAML)
    - helm/chart/templates/rbac-role.yaml (ClusterRole from +kubebuilder:rbac)
    - api/v1alpha1/zz_generated.deepcopy.go

    Runs controller-gen inside a Docker container
    (build/docker/Dockerfile.cleanroom-operator-manifests) so no local Go
    toolchain is required. Generated files are copied back to the
    repo for git tracking.
#>

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$operatorDir = "$root/src/cleanroom-operator"
$imageName = "cleanroom-operator-generate:local"

Write-Host "Building generator image..."
docker build -t $imageName `
    -f $root/build/docker/Dockerfile.cleanroom-operator-manifests $root

Write-Host "Extracting generated files..."
$cid = docker create $imageName
try {
    # CRD YAML
    $crdDir = "$operatorDir/helm/chart/templates/crds"
    docker cp "${cid}:/output/crds/." $crdDir

    # FlexNodeClaim and FlexNodeClass CRDs belong only in the
    # karpenter-provider-accr chart (workload cluster), not the
    # mgmt operator chart. Move them there so they stay in sync
    # with the Go types.
    $karpenterCrdDir = "$operatorDir/helm/karpenter-provider-accr/crds"
    $flexCrds = @(
        "cleanroom.azure.com_flexnodeclaims.yaml",
        "cleanroom.azure.com_flexnodeclasses.yaml"
    )
    foreach ($name in $flexCrds) {
        $src = "$crdDir/$name"
        if (Test-Path $src) {
            Move-Item -Path $src -Destination "$karpenterCrdDir/$name" -Force
            Write-Host "Moved $name -> karpenter-provider-accr chart"
        }
    }

    # RBAC ClusterRole
    docker cp "${cid}:/output/rbac/role.yaml" `
        "$operatorDir/helm/chart/templates/rbac-role.yaml"

    # Deepcopy
    docker cp "${cid}:/output/zz_generated.deepcopy.go" `
        "$operatorDir/api/v1alpha1/zz_generated.deepcopy.go"
}
finally {
    docker rm $cid
}

Write-Host "Generated files:"
Get-ChildItem $crdDir -Name
Write-Host "  helm/chart/templates/rbac-role.yaml"
Write-Host "  api/v1alpha1/zz_generated.deepcopy.go"
