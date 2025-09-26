[CmdletBinding()]
param
(
    [string] $projectName = "samples-governance-cli",

    [string] $ccfEndpoint,

    [string] $outDir
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

if ($ccfEndpoint -eq $null) {
    $ccf = $(Get-Content $outDir/ccf.json | ConvertFrom-Json)
    $ccfEndpoint = $ccf.endpoint
}

function Get-JwtClaim {
    param (
        [Parameter(Mandatory = $true)]
        [string]$jwtToken,

        [Parameter(Mandatory = $true)]
        [string]$claim
    )

    # Split JWT into its three parts: header, payload, and signature
    $tokenParts = $jwtToken -split '\.'

    if ($tokenParts.Length -ne 3) {
        throw "Invalid JWT token format. A JWT should have three parts."
    }

    # Decode the payload (the second part) from Base64Url to Base64
    $base64Url = $tokenParts[1]
    $base64 = $base64Url -replace '_', '/' -replace '-', '+'
    
    # Pad the Base64 string if necessary
    $padding = 4 - ($base64.Length % 4)
    if ($padding -ne 4) {
        $base64 += '=' * $padding
    }

    # Decode the Base64 string to a JSON string
    $jsonPayload = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($base64))

    # Convert the JSON payload to a PowerShell object
    $payloadObject = $jsonPayload | ConvertFrom-Json

    # Extract the specified claim
    if ($payloadObject.PSObject.Properties[$claim]) {
        return $payloadObject.$claim
    }
    else {
        throw "Claim '$claim' not found in the JWT payload."
    }
}

function Remove-ExistingUser {
    param (
        [string] $oid,
        [string] $projectName
    )

    $users = (az cleanroom governance user-identity show --governance-client $projectName)
    $Script:found = ""
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        $Script:found = ($users | jq -r ".value[].id" | grep $oid)
    }

    if ($Script:found -ceq $oid) {
        $users | jq
        Write-Output "Removing existing user object id $oid before adding again."
        $proposalId = (az cleanroom governance user-identity remove `
                --object-id $oid `
                --governance-client $projectName `
                --query "proposalId" --output tsv)
        az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $projectName
        $users = (az cleanroom governance user-identity show --governance-client $projectName)
        & {
            # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
            $PSNativeCommandUseErrorActionPreference = $false
            $Script:found = ($users | jq -r ".value[].id" | grep $oid)
        }
        if ($Script:found -ceq $oid) {
            $users | jq
            Write-Error "oid $oid was not removed from user identities output: $users"
            exit 1
        }
    }
}

$invitationId = [Guid]::NewGuid().ToString("N")
$tenantId = (az account show --query "tenantId" -o tsv)
$userType = (az account show --query "user.type" -o tsv)
if ($userType -eq "servicePrincipal") {
    $appId = (az account show --query "user.name" -o tsv)
    $oid = (az ad sp show --id $appId --query "id" -o tsv)
    Write-Output "Current logged in service principal app id is: $appId"
    Remove-ExistingUser -oid $oid -projectName $projectName
    Write-Output "Inviting logged in service principal with appId $appId as authorized user in CCF"
    $proposalId = (az cleanroom governance user-identity invitation create `
            --invitation-id $invitationId `
            --username $appId `
            --tenant-id $tenantId `
            --identity-type service-principal `
            --account-type microsoft `
            --governance-client $projectName `
            --query "proposalId" --output tsv)
}
else {
    if ($env:CODESPACES -eq "true") {
        $jwt = az account get-access-token --query accessToken -o tsv
        $oid = Get-JwtClaim -jwtToken $jwt -claim oid
        $email = Get-JwtClaim -jwtToken $jwt -claim upn
    }
    else {
        $oid = az ad signed-in-user show --query id --output tsv
        $email = az ad signed-in-user show --query userPrincipalName --output tsv
    }

    Write-Output "Current logged in user email is: $email"
    Remove-ExistingUser -oid $oid -projectName $projectName
    Write-Output "Inviting logged in user $email as authorized user in CCF"
    $proposalId = (az cleanroom governance user-identity invitation create `
            --invitation-id $invitationId `
            --username $email `
            --identity-type user `
            --account-type microsoft `
            --governance-client $projectName `
            --query "proposalId" --output tsv)
}

az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $projectName

Write-Output "Starting cgs-client for the logged in user/service principal and checking API output"
$userProjectName = "ccf-invite-azure-user"
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-azlogin-identity `
    --service-cert $outDir/service_cert.pem `
    --name $userProjectName

Write-Output "Accepting invitation $invitationId via $userProjectName..."
az cleanroom governance user-identity invitation accept `
    --invitation-id $invitationId `
    --governance-client $userProjectName

az cleanroom governance user-identity invitation show `
    --invitation-id $invitationId `
    --governance-client $userProjectName

Write-Output "Finalizing the invitation $invitationId via $projectName..."
$proposalId = (az cleanroom governance user-identity add `
        --accepted-invitation-id $invitationId `
        --governance-client $projectName `
        --query "proposalId" --output tsv)
az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $projectName

$invite = (az cleanroom governance user-identity invitation show `
        --invitation-id $invitationId `
        --governance-client $userProjectName | ConvertFrom-Json)
Write-Output "Invitation output:"
$invite | ConvertTo-Json | jq

if ($invite.userInfo.data.tenantId -ne $tenantId) {
    Write-Error "Tenant id $tenantId was not found in invitation output."
    exit 1
}

Write-Output "Get user output:"
az cleanroom governance user-identity show --identity-id $oid --governance-client $userProjectName

Write-Output "List users output:"
$users = (az cleanroom governance user-identity show --governance-client $userProjectName)
$users | jq
$found = ($users | jq -r ".value[].id" | grep $oid)
if ($found -cne $oid) {
    Write-Error "oid $oid was not found user identities output: $users"
    exit 1
}
else {
    Write-Output "Logged in user was able to invoke CCF endpoint and authenticate itself."
}