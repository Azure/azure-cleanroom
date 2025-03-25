function Get-Document {
    [CmdletBinding()]
    param
    (
        [string]
        $id = "",

        [switch]
        $all,

        [string]
        $port = ""
    )

    . $PSScriptRoot/common.ps1

    $port = GetPortOrDie($port)

    if ($all) {
        $response = (curl -sS -X GET localhost:$port/documents)
        return $response
    }

    if ($id -eq "") {
        throw "-all or -id must be specified."
    }

    $response = (curl -sS -X GET localhost:$port/documents/$id)
    return $response
}