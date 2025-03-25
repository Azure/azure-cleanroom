function Propose-DeploymentSpec {
    [CmdletBinding()]
    param
    (
        [string]
        [Parameter(Mandatory)]
        $contractId,

        [string]
        [Parameter(Mandatory)]
        $spec,

        [string]
        $port = ""
    )

    . $PSScriptRoot/common.ps1

    $port = GetPortOrDie($port)
    curl -sS -X POST localhost:$port/contracts/$contractId/deploymentspec/propose -k -H "Content-Type: application/json" -d $spec
}

function Propose-DeploymentSpec-From-File {
    [CmdletBinding()]
    param
    (
        [string]
        [Parameter(Mandatory)]
        $contractId,

        [string]
        [Parameter(Mandatory)]
        $specFilePath,

        [string]
        $port = ""
    )

    # When the body of a proposal gets too big, curl fails with "Argument list too long". To avoid this,
    # using a file-path as the body - https://stackoverflow.com/a/54091092/1898437 

    . $PSScriptRoot/common.ps1

    $port = GetPortOrDie($port)
    curl -sS -X POST localhost:$port/contracts/$contractId/deploymentspec/propose -k -H "Content-Type: application/json" -d "@$specFilePath"
}