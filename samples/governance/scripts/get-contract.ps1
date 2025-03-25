function Get-Contract {
  [CmdletBinding()]
  param
  (
    [string]
    $id = "",

    [switch]
    $all,

    [string]
    $port = "",

    [hashtable]
    $headers = @{"Content-Type" = "application/json" }
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  if ($all) {
    $response = Invoke-WebRequest -Method GET -Uri "http://localhost:$port/contracts" -Headers $headers
    return $response.Content
  }

  if ($id -eq "") {
    throw "-all or -id must be specified."
  }

  $response = Invoke-WebRequest -Method GET -Uri "http://localhost:$port/contracts/$id" -Headers $headers
  return $response.Content
}