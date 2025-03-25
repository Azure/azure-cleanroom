function Propose-Contract-RuntimeOption {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $contractId,

    [string]
    [Parameter(Mandatory)]
    [ValidateSet('logging', 'telemetry')]
    $option,

    [string]
    [Parameter(Mandatory)]
    [ValidateSet('enable', 'disable')]
    $action,

    [string]
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  curl -sS -X POST localhost:$port/contracts/$contractId/$option/propose-$action -k -H "Content-Type: application/json"
}