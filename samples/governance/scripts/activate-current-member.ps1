function Activate-Current-Member {
  [CmdletBinding()]
  param
  (
    [string]
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  Write-Host "Member status:"
  curl -sS -X GET localhost:$port/members | jq

  Write-Host "Activating member..."
  curl -sS -X POST localhost:$port/members/statedigests/ack
  curl -sS -X GET localhost:$port/members | jq
}