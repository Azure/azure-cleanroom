function Propose-CleanRoom-Policy {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $contractId,

    [string]
    $policy = "",

    [switch]
    $allowAll,

    [string]
    $port = "",

    [hashtable]
    $headers = @{"Content-Type" = "application/json" }
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  if (!$allowAll -And $policy -eq "") {
    throw "Either the policy or allowAll flag must be specified."
  }

  if ($allowAll -And $policy -ne "") {
    throw "Both the policy and allowAll flag cannot be specified together."
  }

  if ($allowAll) {
    $policy =
    @"
{
  "type": "add",
  "claims": {
    "x-ms-sevsnpvm-is-debuggable": false,
    "x-ms-sevsnpvm-hostdata": "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"
  }
}
"@
  }

  $response = Invoke-WebRequest -Method POST -Uri "http://localhost:$port/contracts/$contractId/cleanroompolicy/propose" -Headers $headers -Body $policy
  return $response.Content
}