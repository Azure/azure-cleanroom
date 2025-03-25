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

# Delete resources
foreach ($resource in $resource_list) {
    Write-Host "Deleting following resource group:" $resource.name
    az group delete -n $resource.name --yes --no-wait
}