[CmdletBinding()]
param
(
    [string]
    $destinationFolder = ""
)

if ($destinationFolder -ne "-") {
    mkdir -p $destinationFolder
}
$containerNames = (docker ps -a --filter "label=ccf-network/type=node" --format json | jq -r ".Names")
foreach ($containerName in $containerNames) {
    if ($destinationFolder -eq "-") {
        docker cp -L ${containerName}:/app/cchost.log ./cchost.log
        cat ./cchost.log
    }
    else {
        docker logs ${containerName} > ${destinationFolder}/${containerName}_console.log
        docker cp -L ${containerName}:/app/cchost.log ${destinationFolder}/${containerName}_cchost.log
    }
}