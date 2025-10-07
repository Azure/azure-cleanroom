[CmdletBinding()]
param
(
    [string] $projectName = "samples-governance-cli",

    [string] $ccfEndpoint,

    [Parameter(Mandatory = $true)]
    [string] $email,

    [string] $outDir
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

if ($ccfEndpoint -eq "") {
    $ccf = $(Get-Content $outDir/ccf.json | ConvertFrom-Json)
    $ccfEndpoint = $ccf.endpoint
}

Write-Output "Starting cgs-client for $email (device code flow)..."
# Replace any character that is NOT a letter, digit, hyphen, or underscore with an underscore
$userProjectName = "ccf-" + ($email -replace '[^a-zA-Z0-9\-_]', '_')
az cleanroom governance client deploy `
    --ccf-endpoint $ccfEndpoint `
    --use-microsoft-identity `
    --service-cert $outDir/service_cert.pem `
    --name $userProjectName

$username = az cleanroom governance client show `
    --name $userProjectName `
    --query "userTokenClaims.preferred_username" `
    --output tsv
if ($username -ne $email) {
    throw "User $username that was logged in is not the same as the -email parameter value $email"
}

$invitationId = [Guid]::NewGuid().ToString("N")
Write-Output "Inviting $email as authorized user in CCF"
$proposalId = (az cleanroom governance user-identity invitation create `
        --invitation-id $invitationId `
        --username $email `
        --identity-type user `
        --account-type microsoft `
        --governance-client $projectName `
        --query "proposalId" --output tsv)

az cleanroom governance proposal vote --proposal-id $proposalId --action accept --governance-client $projectName

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

az cleanroom governance user-identity invitation show `
    --invitation-id $invitationId `
    --governance-client $userProjectName

$users = (az cleanroom governance user-identity show --governance-client $userProjectName)
$users | jq
