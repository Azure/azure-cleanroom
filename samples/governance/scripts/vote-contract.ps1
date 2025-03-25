function Vote-Contract {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $id,

    [string]
    [Parameter(Mandatory)]
    $proposalId,

    [string]
    [Parameter(Mandatory)]
    [ValidateSet('accept', 'reject')]
    $vote,

    [string]
    $port = "",

    [hashtable]
    $headers = @{"Content-Type" = "application/json" }
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  $data = @"
  {
    "proposalId": "$proposalId"
  }
"@

  $vote_method = $vote -eq "accept" ? "vote_accept" : "vote_reject"
  $response = Invoke-WebRequest -Method POST -Uri "http://localhost:$port/contracts/$id/$vote_method" -Headers $headers -Body $data
  return $response.Content
}