[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [ValidateSet("bvt", "pr")]
    [string]
    $environment
)

$retentionDays = 2

if ($environment -eq "pr") {
    $mhsms = @(
        "prcovidtrainingmhsm4"
    )
}
else {
    $mhsms = @(
        "bvthsm2sea"
    )
}

$resourceGroups = az group list --query "[?tags.SkipCleanup != 'true']" | ConvertFrom-Json

$currentDate = Get-Date -AsUTC
$rgsToDelete = @()

foreach ($rg in $resourceGroups) {
    $createdDate = [DateTime]::ParseExact("$($rg.tags.Created)", "yyyyMMdd", $null)
    if ($($currentDate - $createdDate).Days -gt $retentionDays) {
        $rgsToDelete += $rg.name
    }
}

Write-Host "The following RGs will be deleted"
Write-Host $($rgsToDelete -join "`n")

foreach ($rg in $rgsToDelete) {
    az group delete --name $rg --no-wait --yes
}

foreach ($mhsm in $mhsms) {
    $keys = az keyvault key list --hsm-name $mhsm | ConvertFrom-Json
    foreach ($key in $keys) {
        if ($($currentDate - $key.attributes.created.ToUniversalTime()).Days -gt $retentionDays) {
            Write-Host "Deleting key $($key.name) from MHSM $mhsm"
            az keyvault key delete --name $key.name --hsm-name $mhsm
        }
    }

    # Cleanup stale role assignments.
    # Exclude the PR/BVT object Id otherwise we end up with an unusable HSM.
    $spDetails = (az ad sp show --id $env:AZURE_CLIENT_ID) | ConvertFrom-Json
    $objectId = $spDetails.id
    $assignmentsToCleanup = (az keyvault role assignment list --scope / --hsm-name $mhsm --role "Managed HSM Crypto User" --query "[?principalId != '$objectId']" | ConvertFrom-Json) | Select-Object -Property id
    if ($null -ne $assignmentsToCleanup) {
        Write-Host "Cleaning up following stale assignments on {$mhsm}: $($assignmentsToCleanup.id)"
        az keyvault role assignment delete --ids $($assignmentsToCleanup.id) --hsm-name $mhsm
    }
}
