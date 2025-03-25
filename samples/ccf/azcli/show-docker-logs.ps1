[CmdletBinding()]
param
(
    [string]
    $label = "ccf-network/type=node"
)
$containerNames = (docker ps -a --filter "label=$label" --format json | jq -r ".Names")
foreach ($containerName in $containerNames) {
    docker logs ${containerName}
}