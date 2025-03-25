function Set-Contract-RuntimeOption {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $contractId,

    [string]
    [Parameter(Mandatory)]
    [ValidateSet('execution')]
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

  curl -sS -X POST localhost:$port/contracts/$contractId/${option}/${action} -k -H "Content-Type: application/json"
}