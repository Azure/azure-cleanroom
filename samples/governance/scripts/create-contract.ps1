function Create-Contract {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $id,

    [string]
    [Parameter(Mandatory)]
    $data,

    [string]
    $version = "",

    [string]
    $port = "",

    [hashtable]
    $headers = @{"Content-Type" = "application/json" }
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  $contract = @{
    version = $version
    data    = $data
  }

  $contract = $contract | ConvertTo-Json

  Invoke-WebRequest -Method PUT -Uri "http://localhost:$port/contracts/$id" -Headers $headers -Body $contract
}
