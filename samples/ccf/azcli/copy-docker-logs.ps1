[CmdletBinding()]
param
(
    [string]
    $destinationFolder = ""
)
mkdir -p $destinationFolder
$containerNames = (docker ps -a --filter "label=ccf-network/type=node" --format json | jq -r ".Names")
foreach ($containerName in $containerNames) {
    docker logs ${containerName} > ${destinationFolder}/${containerName}_console.log
    docker cp -L ${containerName}:/app/logs/cchost.log ${destinationFolder}/${containerName}_cchost.log
}