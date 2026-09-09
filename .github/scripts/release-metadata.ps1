<#
.SYNOPSIS
    Publishes the versioned release-metadata catalog for a release.

.DESCRIPTION
    Resolves image digests from the release ACR, packages the release-metadata Helm
    chart (carrying forward images not released this cycle), uploads the .tgz as a
    GitHub Release asset, and opens a PR updating index.yaml on the pages branch via
    chart-releaser (cr). cr only creates the PR; a human approves and merges it
    (enforced by branch protection). Runs inside the release-metadata composite
    action; expects GH_TOKEN in the environment.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$tag,

    [Parameter(Mandatory = $true)]
    [string]$environment,

    [Parameter(Mandatory = $true)]
    [string]$registryName,

    [Parameter(Mandatory = $true)]
    [string]$pagesBranch
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$owner = $env:GITHUB_REPOSITORY_OWNER
$repoName = ($env:GITHUB_REPOSITORY -split '/')[1]
$crVersion = "1.8.1"
$crDir = "$env:RUNNER_TEMP/crbin"
$packagePath = "$env:RUNNER_TEMP/cr-release-packages"

# Digests are resolved from the release ACR; published references point at public
# MCR for the 'unlisted' environment (matches release-*-version-document.ps1).
$digestRepo = "$registryName.azurecr.io/$environment/azurecleanroom"
$publishRepo = if ($environment -eq "unlisted") {
    "mcr.microsoft.com/azurecleanroom"
}
else {
    $digestRepo
}

# Log in to the release ACR so oras (Get-Digest) can resolve image digests.
az acr login --name $registryName

# Install chart-releaser (cr).
New-Item -ItemType Directory -Force $crDir | Out-Null
$crUrl = "https://github.com/helm/chart-releaser/releases/download/v$crVersion/" +
"chart-releaser_${crVersion}_linux_amd64.tar.gz"
Invoke-WebRequest -Uri $crUrl -OutFile "$crDir/cr.tgz"
tar -xz -C $crDir -f "$crDir/cr.tgz" cr
$cr = "$crDir/cr"

# cr needs origin/<pages-branch> to worktree the current index and branch off it.
git fetch origin "${pagesBranch}:refs/remotes/origin/$pagesBranch"

# Resolve digests from the ACR, bake public references, and package the .tgz (cr owns
# index generation). Every release rebuilds the full set, so the whole catalog is
# resolved fresh at $tag.
& "$root/build/build-release-metadata-chart.ps1" `
    -tag $tag `
    -repo $digestRepo `
    -publishRepo $publishRepo `
    -outDir $packagePath

# Attach the chart .tgz to the (already-created) Release for this tag. Skip upload if
# it already exists (never overwrite a published, digest-referenced asset). Then use
# the PUBLISHED asset's exact bytes for indexing + attestation, so a re-run (which
# rebuilds a non-byte-identical .tgz) stays consistent with what consumers download.
$asset = "release-metadata-$tag.tgz"
$existing = gh release view $tag --json assets --jq '.assets[].name'
if (@($existing) -contains $asset) {
    Write-Host "Asset $asset already present on release $tag; skipping upload."
}
else {
    gh release upload $tag "$packagePath/$asset"
}
gh release download $tag --repo $env:GITHUB_REPOSITORY --pattern $asset --dir $packagePath --clobber

# Keep at most one outstanding index PR: skip if one is already open. (An
# already-merged version is a no-op inside cr, which adds only missing versions.)
$openPrs = gh pr list --repo $env:GITHUB_REPOSITORY --state open --base $pagesBranch `
    --json headRefName --jq '[.[] | select(.headRefName | startswith("chart-releaser-"))] | length'
if ([int]$openPrs -gt 0) {
    Write-Host "An index update PR is already open; skipping to avoid duplicates."
    return
}

# Commit the index update as github-actions[bot] (which also opens the PR).
git config --global user.name "github-actions[bot]"
git config --global user.email "41898282+github-actions[bot]@users.noreply.github.com"

# Open the PR. --index-path is a local scratch copy; the published index is written
# on the pages branch via --pages-index-path.
& $cr index `
    --owner $owner `
    --git-repo $repoName `
    --token $env:GH_TOKEN `
    --package-path $packagePath `
    --release-name-template "{{ .Version }}" `
    --pages-branch $pagesBranch `
    --pages-index-path index.yaml `
    --index-path "$env:RUNNER_TEMP/index.yaml" `
    --pr
