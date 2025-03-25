function Propose-RuntimeOption {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    [ValidateSet('autoapprove-constitution-proposal', 'autoapprove-jsapp-proposal', 'autoapprove-deploymentspec-proposal', 'autoapprove-cleanroompolicy-proposal')]
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

  curl -sS -X POST localhost:$port/runtimeoptions/$option/propose-$action -k -H "Content-Type: application/json"
}