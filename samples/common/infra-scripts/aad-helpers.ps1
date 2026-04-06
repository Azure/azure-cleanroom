function GetLoggedInEntityObjectId {
    if ($env:GITHUB_ACTIONS -eq "true") {
        Write-Host "Running inside GitHub Actions. Fetching Azure credentials"
        $clientId = $env:AZURE_CLIENT_ID

        $spDetails = (az ad sp show --id $clientId) | ConvertFrom-Json
        $objectId = $spDetails.id

        Write-Host "Fetched object ID $objectId for client ID $clientID"
        return $objectId
    }
    else {
        Write-Host "Fetching object ID of currently logged in user"
        if ($env:CODESPACES -eq "true" -or $env:DEVCONTAINER -eq "true") {
            # Since some tenant (including Microsoft tenant) has Conditional Access policies that block
            # accessing Microsoft Graph with device code (#22629), querying Microsoft Graph API is no
            # longer possible with device code.
            # Using manual workaround per https://github.com/Azure/azure-cli/issues/22776
            Write-Host "Running in Codespaces so extracting object ID from access token"
            Write-Host "$(pip3 install --upgrade pyjwt)"
            $objectId = (az account get-access-token --query accessToken --output tsv | `
                    tr -d '\n' | `
                    python -c "import jwt, sys; print(jwt.decode(sys.stdin.read(), algorithms=['RS256'], options={'verify_signature': False})['oid'])")
            return $objectId
        }
        else {
            $result = (az ad signed-in-user show) | ConvertFrom-Json
            return $result.id
        }
    }
}

function Get-Assignee-Principal-Type {
    if ($env:GITHUB_ACTIONS -eq "true") {
        return "ServicePrincipal"
    }
    else {
        return "User"
    }
}