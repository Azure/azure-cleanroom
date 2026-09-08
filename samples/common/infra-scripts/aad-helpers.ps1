function GetLoggedInEntityObjectId {
    if ($env:GITHUB_ACTIONS -eq "true") {
        Write-Host "Running inside GitHub Actions. Fetching Azure credentials"
        $clientId = $env:AZURE_CLIENT_ID

        # Derive the logged-in service principal's objectId from the access
        # token's `oid` claim instead of `az ad sp show --id <clientId>`. The two
        # return the same value for an SP login, but `az ad sp show` requires
        # Microsoft Graph directory-read permission on the caller — which the
        # release identity (cleanroom-emu-bvt-mi, used by the nginx-hello BVT for
        # its ACR policy push) does NOT have, failing with "Insufficient
        # privileges to complete the operation." The token's `oid` needs no Graph
        # access, so this works for any Azure-RBAC-scoped identity. Decoded in
        # pure PowerShell (no PyJWT dependency).
        $objectId = $null
        try {
            $accessToken = (az account get-access-token --query accessToken --output tsv)
            $payload = $accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/')
            switch ($payload.Length % 4) { 2 { $payload += '==' } 3 { $payload += '=' } }
            $claims = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($payload)) | ConvertFrom-Json
            $objectId = $claims.oid
        }
        catch {
            Write-Host "Could not extract oid from token ($_); falling back to 'az ad sp show'."
        }

        if ([string]::IsNullOrWhiteSpace($objectId)) {
            # Fallback to Graph for identities that DO have directory-read (e.g.
            # cleanroom-pr-oidc) in case token parsing is unavailable.
            $spDetails = (az ad sp show --id $clientId) | ConvertFrom-Json
            $objectId = $spDetails.id
        }

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