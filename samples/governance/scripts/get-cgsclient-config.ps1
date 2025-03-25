Function Get-CgsClient-Config {
  [CmdletBinding()]
  param
  (
    [string]
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  curl -sS -X GET localhost:$port/show | jq
}