function Vote-MemberDocument {
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
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  $data = @"
  {
    "proposalId": "$proposalId"
  }
"@

  $vote_method = $vote -eq "accept" ? "vote_accept" : "vote_reject"
  $response = (curl -sS -X POST localhost:$port/memberdocuments/$id/$vote_method -k -H "Content-Type: application/json" -d $data)
  return $response
}