function Create-MemberDocument {
    [CmdletBinding()]
    param
    (
        [string]
        [Parameter(Mandatory)]
        $id,

        [string]
        [Parameter(Mandatory)]
        $contractId,

        [string]
        [Parameter(Mandatory)]
        $data,

        [string]
        $version = "",

        [string]
        $port = ""
    )

    . $PSScriptRoot/common.ps1

    $port = GetPortOrDie($port)

    $document = @{
        version    = $version
        contractId = $contractId
        data       = $data
    }

    $document = $document | ConvertTo-Json
    curl -sS -D - -X PUT localhost:$port/memberdocuments/$id -k -H "Content-Type: application/json" -d $document
}
