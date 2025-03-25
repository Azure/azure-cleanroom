function Get-DeploymentSpec {
    [CmdletBinding()]
    param
    (
        [string]
        $contractId,

        [string]
        $port = ""
    )

    . $PSScriptRoot/common.ps1

    $port = GetPortOrDie($port)

    $response = (curl -sS -X GET localhost:$port/contracts/$contractId/deploymentspec)
    return $response
}