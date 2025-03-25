function Check-RuntimeOption {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    [ValidateSet('autoapprove-constitution-proposal', 'autoapprove-jsapp-proposal', 'autoapprove-deploymentspec-proposal', 'autoapprove-cleanroompolicy-proposal')]
    $statusOf,

    [string]
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  $response = (curl -sS -X POST localhost:$port/runtimeoptions/checkstatus/$statusOf -d "")
  return $response
}
