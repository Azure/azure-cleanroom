function Get-CleanRoom-Policy {
    [CmdletBinding()]
    param
    (
        [string]
        $contractId,

        [string]
        $port = "",

        [hashtable]
        $headers = @{"Content-Type" = "application/json" }
    )

    . $PSScriptRoot/common.ps1

    $port = GetPortOrDie($port)

    $response = Invoke-WebRequest -Method GET -Uri "http://localhost:$port/contracts/$contractId/cleanroompolicy" -Headers $headers
    return $response.Content
}