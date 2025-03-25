function Check-Contract-RuntimeOption {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $contractId,

    [string]
    [Parameter(Mandatory)]
    [ValidateSet('execution', 'logging', 'telemetry')]
    $statusOf,

    [string]
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  $response = (curl -sS -X POST localhost:$port/contracts/$contractId/checkstatus/$statusOf -d "")
  return $response
}
