[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [ValidateSet("bvt", "pr")]
    [string]
    $environment,

    [switch]
    $dryRun
)

$retentionDays = 2

if ($environment -eq "pr") {
    $mhsms = @(
        "prcovidtrainingmhsm4",
        "azcleanroomemuhsm2"
    )
}
else {
    $mhsms = @(
        "bvthsm2sea",
        "azcleanroombvthsm2"
    )
}

$principalIdsToExclude = @(
    "ff50ca9a-012f-4739-bc6b-a92d42b08a00", # cleanroom-emu-pr-mi
    "8d6fe26a-4a14-4f34-b0a4-054f157ca75b", # cleanroom-emu-bvt-mi
    "6afcd0e0-8db1-45c1-932a-f22c7a035647", # azcleanroom-ctest-mi
    "b058a7d3-92d3-482a-9ad5-f3089598fb41", # cleanroom-public-bvt-mi
    "d3d0ba3a-1d41-48a5-af04-8d4130770780", # cleanroom-public-pr-mi
    "27f2974f-1e56-497c-b780-4782c71853b8" # cleanroomprivaterpapp
)

$resourceGroups = az group list --query "[?tags.SkipCleanup != 'true']" | ConvertFrom-Json

$currentDate = Get-Date -AsUTC
$rgsToDelete = @()

foreach ($rg in $resourceGroups) {
    $createdDate = [DateTime]::ParseExact("$($rg.tags.Created)", "yyyyMMdd", $null)
    if ($($currentDate - $createdDate).Days -gt $retentionDays) {
        $rgsToDelete += $rg.name
    }
}
Write-Host "The following $($rgsToDelete.Count) RGs will be deleted"
Write-Host $($rgsToDelete -join "`n")

Write-Host "Enumerating service principal type role assignments..."
# Get all role assignments for service principals
$assignments = az role assignment list --all --query "[?principalType=='ServicePrincipal']" | ConvertFrom-Json
Write-Host "Checking for stale role assignments out of $($assignments.Count) items..."

# Collect unique principalIds
$principalIds = $assignments.principalId | Sort-Object -Unique
Write-Host "Enumerating missing service principals out of $($principalIds.Count) items..."

# HashSet of missing principals
$missingPrincipals = @{}
$index = 1
foreach ($principalId in $principalIds) {
    # Use Carriage Return to overwrite the current line so that we show progress on one line.
    Write-Host "`rChecking $index/$($principalIds.Count)..." -NoNewline
    if ($principalIdsToExclude -contains $principalId) {
        $index++
        continue
    }
    $sp = az ad sp show --id $principalId 2>$null
    if (-not $sp) {
        $missingPrincipals[$principalId] = $true
    }
    $index++
}
Write-Host  # Move to next line after loop

Write-Host "$($missingPrincipals.Count) missing principals were found."
$rasToDelete = @()
$skippedRoleAssignments = @()
foreach ($a in $assignments) {
    if ($missingPrincipals.ContainsKey($a.principalId)) {
        # Check if the role assignment is older than retention days
        if ($a.createdOn) {
            try {
                $createdDate = [DateTime]::Parse($a.createdOn)
                if ($($currentDate - $createdDate).Days -gt $retentionDays) {
                    $rasToDelete += $a
                }
            }
            catch {
                # If date parsing fails, default to safety behavior
                Write-Host "Warning: Invalid creation date format for role assignment $($a.id): $($a.createdOn), skipping deletion for safety"
                $skippedRoleAssignments += $a.id
            }
        }
        else {
            # If no creation date available, default to the existing behavior for safety
            Write-Host "Warning: No creation date found for role assignment $($a.id), skipping deletion for safety"
            $skippedRoleAssignments += $a.id
        }
    }
}

Write-Host "$($rasToDelete.Count) role assignments will be deleted."

# Log skipped role assignments for manual verification
if ($skippedRoleAssignments.Count -gt 0) {
    Write-Host ""
    Write-Host "$($skippedRoleAssignments.Count) role assignments were skipped due to missing/invalid creation dates:"
    foreach ($skippedId in $skippedRoleAssignments) {
        Write-Host "  - $skippedId"
    }
    Write-Host "These assignments may need manual verification and cleanup."
    Write-Host ""
}

if ($dryRun) {
    exit 0
}

foreach ($rg in $rgsToDelete) {
    Write-Host "Deleting resource group $rg"
    az group delete --name $rg --no-wait --yes
}

foreach ($a in $rasToDelete) {
    $principalId = $a.principalId
    $role = $a.roleDefinitionName
    $scope = $a.scope
    # rasToDelete already contains assignments with valid createdOn dates since they're filtered
    $createdOn = $a.createdOn

    Write-Host "Removing stale role assignment: $role at $scope for $principalId (created: $createdOn)"
    az role assignment delete --assignee-object-id $principalId --role $role --scope $scope
}

pwsh $PSScriptRoot/remove-old-buckets.ps1 `
    -Prefix "consumer-input-big-data-query-analytics-" `
    -MaxAgeDays $retentionDays

pwsh $PSScriptRoot/remove-old-buckets.ps1 `
    -Prefix "consumer-output-big-data-query-analytics-" `
    -MaxAgeDays $retentionDays

foreach ($mhsm in $mhsms) {
    $keys = az keyvault key list --hsm-name $mhsm | ConvertFrom-Json
    foreach ($key in $keys) {
        # Convert Unix epoch timestamp to DateTime
        $createdDate = [DateTimeOffset]::FromUnixTimeSeconds($key.attributes.created).UtcDateTime
        if ($($currentDate - $createdDate).Days -gt $retentionDays) {
            Write-Host "Deleting key $($key.name) from MHSM $mhsm"
            az keyvault key delete --name $key.name --hsm-name $mhsm
        }
    }

    # Cleanup stale role assignments.
    # Exclude the PR/BVT object Id otherwise we end up with an unusable HSM.
    $spDetails = (az ad sp show --id $env:AZURE_CLIENT_ID) | ConvertFrom-Json
    $objectId = $spDetails.id
    $assignmentsToCleanup = (az keyvault role assignment list --scope / --hsm-name $mhsm --role "Managed HSM Crypto User" --query "[?principalId != '$objectId']" | ConvertFrom-Json)

    if ($null -ne $assignmentsToCleanup -and @($assignmentsToCleanup).Count -gt 0) {
        Write-Host "Found stale assignments on MHSM { $mhsm } to clean up"
        
        # Convert to array if it's not already (handles single item case)
        $assignmentsArray = @($assignmentsToCleanup)
        
        foreach ($assignment in $assignmentsArray) {
            Write-Host "Deleting role assignment ID: $($assignment.id)"
            az keyvault role assignment delete --hsm-name $mhsm --ids $($assignment.id)
        }
    }
}
