Function Get-Member {
  [CmdletBinding()]
  param
  (
    [string]
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  curl -sS -X GET localhost:$port/members | jq
}