function Get-MemberDocument {
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
        $response = (curl -sS -X GET localhost:$port/memberdocuments)
        return $response
    }

    if ($id -eq "") {
        throw "-all or -id must be specified."
    }

    $response = (curl -sS -X GET localhost:$port/memberdocuments/$id)
    return $response
}