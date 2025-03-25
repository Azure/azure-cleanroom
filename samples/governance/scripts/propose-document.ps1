function Propose-Document {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $id,

    [string]
    [Parameter(Mandatory)]
    $version,

    [string]
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  $data = @"
  {
    "version": "$version"
  }
"@
  $response = (curl -sS -X POST localhost:$port/documents/$id/propose -k -H "Content-Type: application/json" -d $data)
  return $response
}
