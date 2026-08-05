[CmdletBinding()]
param
(
    [string]
    $tag = ""
)

if ($tag -eq "") {
    throw "Specify the tag to locate resource groups to delete."
}

$resource_list = (az group list --tag $tag | ConvertFrom-Json)
Write-Host "Found" $resource_list.Count "resources"

# Delete resources. Skip any RG explicitly tagged SkipCleanup=true so that
# pre-loaded data is preserved even if it accidentally matches the tag filter.
foreach ($resource in $resource_list) {
    if ($resource.tags.SkipCleanup -eq "true") {
        Write-Host "Skipping resource group (SkipCleanup=true):" $resource.name
        continue
    }
    Write-Host "Deleting following resource group:" $resource.name
    az group delete -n $resource.name --yes --no-wait
}