function Propose-Contract {
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
    $port = "",

    [hashtable]
    $headers = @{"Content-Type" = "application/json" }
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  $data = @"
  {
    "version": "$version"
  }
"@

  $response = Invoke-WebRequest -Method POST -Uri "http://localhost:$port/contracts/$id/propose" -Headers $headers -Body $data
  return $response.Content
}
