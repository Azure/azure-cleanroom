function Add-Member {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $memberCertPemFilePath,

    [string]
    [Parameter(Mandatory)]
    $identifier,

    [string]
    $tenantId = "",

    [string[]]
    [ValidateSet("cgsOperator", "contractOperator")]
    $addRole,

    [string[]]
    [ValidateSet("cgsOperator", "contractOperator")]
    $removeRole,

    [string]
    $port = ""
  )

  . $PSScriptRoot/common.ps1

  $port = GetPortOrDie($port)

  $cgsRoles = @{}
  for ($i = 0; $i -lt $addRole.Count; ++$i) {
    $cgsRoles.Add($addRole[$i], "true")
  }

  for ($i = 0; $i -lt $removeRole.Count; ++$i) {
    $cgsRoles.Add($removeRole[$i], "false")
  }

  $certContent = (Get-Content $memberCertPemFilePath -Raw).ReplaceLineEndings("\n")
  $memberdata = @{}
  if ($tenantId -ne "") {
    $memberData.Add("tenantId", $tenantId)
  }
  if ($identifier -ne "") {
    $memberData.Add("identifier", $identifier)
  }
  if ($cgsRoles.Count -gt 0) {
    $memberData.Add("cgsRoles", $cgsRoles)
  }

  $memberData = $memberData | ConvertTo-Json -Compress
  Write-Host "Member data:"
  $memberData | jq

  $proposal = @"
    {
    "actions": [{
        "name": "set_member",
        "args": {
            "cert": "$certContent",
            "member_data": $memberData
        }
    }]
    }
"@

  Write-Output "Submitting set_member proposal"
  $proposalId = (curl -sS -X POST -H "content-type: application/json" localhost:$port/proposals/create -d $proposal | jq -r '.proposalId')
  curl -sS -X POST localhost:$port/proposals/$proposalId/ballots/vote_accept | jq
}