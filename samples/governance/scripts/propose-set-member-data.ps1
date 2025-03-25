function Propose-Set-MemberData {
  [CmdletBinding()]
  param
  (
    [string]
    [Parameter(Mandatory)]
    $memberId,

    [string]
    $tenantId = "",

    [string]
    $memberIdentifier = "",

    [string[]]
    [ValidateSet("cgsOperator", "contractOperator")]
    $addRole,

    [string[]]
    [ValidateSet("cgsOperator", "contractOperator")]
    $removeRole,

    [switch]
    $y,

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

  $memberdata = @{}
  if ($tenantId -ne "") {
    $memberData.Add("tenantId", $tenantId)
  }
  if ($memberIdentifier -ne "") {
    $memberData.Add("identifier", $memberIdentifier)
  }
  if ($cgsRoles.Count -gt 0) {
    $memberData.Add("cgsRoles", $cgsRoles)
  }

  $existingMemberData = (curl -sS "localhost:$port/members" | jq -r --arg memberId "$memberId" '.value[] | select(.memberId==$memberId).memberData')
  if ($existingMemberData -eq "null") {
    $existingMemberData = "{}"
  }

  $inputMemberData = $memberData | ConvertTo-Json -Compress
  $mergedMemberData = (jq -n --argjson existing "$existingMemberData" --argjson new "$inputMemberData" '$existing + $new')
  Write-Host "New member data:"
  $mergedMemberData | jq
  if (!$y) {
    Read-Host "Check the new member data above before continuing. Press Enter to continue or Ctrl+C to quit" | Out-Null
  }

  $response = (curl -sS -X POST -H "content-type: application/json" localhost:$port/proposals/create -d `
      @"
  {
    "actions": [{
       "name": "set_member_data",
       "args": {
        "member_id": "$memberId",
        "member_data": $mergedMemberData
       }
    }]
  }
"@)

  return $response
}