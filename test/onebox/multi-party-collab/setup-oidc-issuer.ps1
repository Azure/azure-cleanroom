param(
    [Parameter(Mandatory = $true)]
    [string]$resourceGroup,
    [Parameter(Mandatory = $true)]
    [string]$governanceClient,
    [Parameter()]
    [ValidateSet("member-tenant", "global", "user")]
    [string]$oidcIssuerLevel = "member-tenant",
    [string]$outDir = "",
    [switch]$useFrontendService,
    [string]$frontendServiceEndpoint = "localhost:61001"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($outDir -eq "") {
    $outDir = "$PSScriptRoot/demo-resources/$resourceGroup"
}
else {
    $outDir = "$outDir/$resourceGroup"
}
. $outDir/names.generated.ps1

$root = git rev-parse --show-toplevel
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

function Setup-OIDC-Issuer-StorageAccount {
    param(
        [string]$resourceGroup,
        [string]$tenantId,
        [string]$outDir,
        [string]$OIDC_STORAGE_ACCOUNT_NAME,
        [string]$OIDC_CONTAINER_NAME,
        [string]$governanceClient,
        [bool]$useFrontendService = $false,
        [string]$frontendServiceEndpoint = ""
    )

    $storageAccountResult = $null
    # for MSFT tenant 72f988bf-86f1-41af-91ab-2d7cd011db47 we must also use pre-provisioned storage account.
    if ($env:USE_PREPROVISIONED_OIDC -eq "true" -or $tenantId -eq "72f988bf-86f1-41af-91ab-2d7cd011db47") {
        Write-Host "Use pre-provisioned storage account for OIDC setup"
        $preprovisionedSAName = "cleanroomoidc"
        $storageAccountResult = (az storage account show `
                --name $preprovisionedSAName) | ConvertFrom-Json

        $status = (az storage blob service-properties show `
                --account-name $preprovisionedSAName `
                --auth-mode login `
                --query "staticWebsite.enabled" `
                --output tsv)
        if ($status -ne "true") {
            throw "Preprovisioned storage account $preprovisionedSAName should have static website enabled."
        }
    }
    else {
        $storageAccountResult = (az storage account create `
                --resource-group "$resourceGroup" `
                --name "${OIDC_STORAGE_ACCOUNT_NAME}" ) | ConvertFrom-Json

        Write-Host "Setting up static website on storage account to setup oidc documents endpoint"
        az storage blob service-properties update `
            --account-name $storageAccountResult.name `
            --static-website `
            --404-document error.html `
            --index-document index.html `
            --auth-mode login
    }

    $objectId = GetLoggedInEntityObjectId
    $role = "Storage Blob Data Contributor"
    $roleAssignment = (az role assignment list `
            --assignee-object-id $objectId `
            --scope $storageAccountResult.id `
            --role $role `
            --fill-principal-name false `
            --fill-role-definition-name false) | ConvertFrom-Json

    if ($roleAssignment.Length -eq 1) {
        Write-Host "$role permission on the storage account already exists, skipping assignment"
    }
    else {
        Write-Host "Assigning $role on the storage account"
        az role assignment create `
            --role $role `
            --scope $storageAccountResult.id `
            --assignee-object-id $objectId `
            --assignee-principal-type $(Get-Assignee-Principal-Type)
    }

    if ($env:GITHUB_ACTIONS -eq "true") {
        & {
            # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
            $PSNativeCommandUseErrorActionPreference = $false
            $timeout = New-TimeSpan -Seconds 120
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $hasAccess = $false
            while (!$hasAccess) {
                # Do container/blob creation check to determine whether the permissions have been applied or not.
                az storage container create --name ghaction-c --account-name $storageAccountResult.name --auth-mode login 1>$null 2>$null
                az storage blob upload --data "teststring" --overwrite -c ghaction-c -n ghaction-b --account-name $storageAccountResult.name --auth-mode login 1>$null 2>$null
                if ($LASTEXITCODE -gt 0) {
                    if ($stopwatch.elapsed -gt $timeout) {
                        throw "Hit timeout waiting for rbac permissions to be applied on the storage account."
                    }
                    $sleepTime = 10
                    Write-Host "Waiting for $sleepTime seconds before checking if storage account permissions got applied..."
                    Start-Sleep -Seconds $sleepTime
                }
                else {
                    Write-Host "Blob creation check returned $LASTEXITCODE. Assuming permissions got applied."
                    $hasAccess = $true
                }
            }
        }
    }

    $webUrl = (az storage account show `
            --name $storageAccountResult.name `
            --query "primaryEndpoints.web" `
            --output tsv)
    Write-Host "Storage account static website URL: $webUrl"

    @"
      {
        "issuer": "$webUrl${OIDC_CONTAINER_NAME}",
        "jwks_uri": "$webUrl${OIDC_CONTAINER_NAME}/openid/v1/jwks",
        "response_types_supported": [
        "id_token"
        ],
        "subject_types_supported": [
        "public"
        ],
        "id_token_signing_alg_values_supported": [
        "RS256"
        ]
      }
"@ > $outDir/openid-configuration.json

    az storage blob upload `
        --container-name '$web' `
        --file $outDir/openid-configuration.json `
        --name ${OIDC_CONTAINER_NAME}/.well-known/openid-configuration `
        --account-name $storageAccountResult.name `
        --overwrite `
        --auth-mode login

    # Fetch JWKS from CCF - use frontend service if available, otherwise use governance client.
    if ($useFrontendService) {
        Write-Host "Fetching OIDC keys via frontend service"
        $userToken = (az cleanroom governance client get-access-token --query accessToken -o tsv --name $governanceClient)
        $collaborationId = $governanceClient
        curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${collaborationId}/oidc/keys?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken" | jq > $outDir/jwks.json
    }
    else {
        Write-Host "Fetching OIDC keys via governance client"
        $ccfEndpoint = (az cleanroom governance client show --name $governanceClient | ConvertFrom-Json)
        $url = "$($ccfEndpoint.ccfEndpoint)/app/oidc/keys"
        curl -s -k $url | jq > $outDir/jwks.json
    }

    az storage blob upload `
        --container-name '$web' `
        --file $outDir/jwks.json `
        --name ${OIDC_CONTAINER_NAME}/openid/v1/jwks `
        --account-name $storageAccountResult.name `
        --overwrite `
        --auth-mode login

    Write-Output $webUrl > $outDir/web-url.txt
}

function Get-Oidc-Issuer {
    param(
        [bool]$useFrontendService,
        [string]$frontendServiceEndpoint,
        [string]$governanceClient,
        [string]$oidcIssuerLevel
    )

    if ($useFrontendService) {
        Write-Host "Getting OIDC Issuer"
        $userToken = (az cleanroom governance client get-access-token --query accessToken -o tsv --name $governanceClient)
        $collaborationId = $governanceClient
        $oidcInfoJson = curl --fail-with-body -sS -X GET `
            "http://${frontendServiceEndpoint}/collaborations/${collaborationId}/oidc/IssuerInfo?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken"

        $oidcInfo = $oidcInfoJson | ConvertFrom-Json
        if ($oidcIssuerLevel -eq "global") {
            return @{ issuerUrl = $oidcInfo.issuerUrl }
        }
        else {
            # For user and member-tenant levels, return tenant-specific issuer URL if available.
            # Note: When using frontend service with JWT auth, the tenant ID in the token may come
            # from a local IDP and won't match `az account show`, so we don't compare tenant IDs here.
            if ($null -ne $oidcInfo.tenantData -and $null -ne $oidcInfo.tenantData.issuerUrl) {
                return @{ issuerUrl = $oidcInfo.tenantData.issuerUrl; tenantId = $oidcInfo.tenantData.tenantId }
            }
            else {
                return $null
            }
        }
    }
    else {
        Write-Host "Getting OIDC Issuer"
        if ($oidcIssuerLevel -eq "global") {
            $oidcInfo = (az cleanroom governance oidc-issuer show `
                    --governance-client $governanceClient | ConvertFrom-Json)
            return @{ issuerUrl = $oidcInfo.issuerUrl }
        }
        else {
            # For user and member-tenant levels, return tenant-specific issuer URL if available.
            # Note: When using local IDP, the tenant ID in the token may come from a local IDP
            # and won't match `az account show`, so we don't compare tenant IDs here.
            $tenantData = (az cleanroom governance oidc-issuer show `
                    --governance-client $governanceClient `
                    --query "tenantData" | ConvertFrom-Json)
            if ($null -ne $tenantData -and $null -ne $tenantData.issuerUrl) {
                return @{ issuerUrl = $tenantData.issuerUrl; tenantId = $tenantData.tenantId }
            }
            else {
                return $null
            }
        }
    }
}

function Confirm-IssuerUrl {
    param(
        [string]$expectedIssuerUrl,
        [string]$actualIssuerUrl
    )

    if ($actualIssuerUrl -ne $expectedIssuerUrl) {
        throw "Issuer URL mismatch: expected '$expectedIssuerUrl' but" +
        " get-oidc-issuer returned '$actualIssuerUrl'."
    }
    Write-Host "Validated get-oidc-issuer returned the expected issuer URL."
}

function Set-Oidc-Issuer-Url {
    param(
        [bool]$useFrontendService,
        [string]$frontendServiceEndpoint,
        [string]$governanceClient,
        [string]$issuerUrl
    )

    if ($useFrontendService) {
        Write-Host "Setting OIDC Issuer URL via frontend service (JWT auth)"
        $userToken = (az cleanroom governance client get-access-token --query accessToken -o tsv --name $governanceClient)
        $collaborationId = $governanceClient
        $body = @{ url = $issuerUrl } | ConvertTo-Json
        curl --fail-with-body -sS -X POST `
            "http://${frontendServiceEndpoint}/collaborations/${collaborationId}/oidc/setIssuerUrl?api-version=2026-03-01-preview" `
            -H "content-type: application/json" `
            -H "Authorization: Bearer $userToken" `
            -d $body
    }
    else {
        Write-Host "Setting OIDC Issuer URL via CLI"
        az cleanroom governance oidc-issuer set-issuer-url `
            --governance-client $governanceClient `
            --url $issuerUrl
    }
}

# Set OIDC issuer.
if ($oidcIssuerLevel -eq "global") {
    $issuerInfo = Get-Oidc-Issuer `
        -useFrontendService $useFrontendService `
        -frontendServiceEndpoint $frontendServiceEndpoint `
        -governanceClient $governanceClient `
        -oidcIssuerLevel $oidcIssuerLevel

    if ($null -ne $issuerInfo -and $null -ne $issuerInfo.issuerUrl) {
        Write-Host -ForegroundColor Yellow "OIDC issuer already set at $oidcIssuerLevel level, skipping."
        $issuerUrl = $issuerInfo.issuerUrl
    }
    else {
        Write-Host "Setting up OIDC issuer at $oidcIssuerLevel level"
        $currentUser = (az account show) | ConvertFrom-Json
        $tenantId = $currentUser.tenantid
        Setup-OIDC-Issuer-StorageAccount `
            -resourceGroup $resourceGroup `
            -tenantId $tenantId `
            -outDir $outDir `
            -OIDC_STORAGE_ACCOUNT_NAME $OIDC_STORAGE_ACCOUNT_NAME `
            -OIDC_CONTAINER_NAME $OIDC_CONTAINER_NAME `
            -governanceClient $governanceClient `
            -useFrontendService $useFrontendService `
            -frontendServiceEndpoint $frontendServiceEndpoint
        $webUrl = Get-Content $outDir/web-url.txt

        $expectedIssuerUrl = "$webUrl${OIDC_CONTAINER_NAME}"
        $proposalId = (az cleanroom governance oidc-issuer propose-set-issuer-url `
                --url $expectedIssuerUrl `
                --governance-client $governanceClient `
                --query "proposalId" `
                --output tsv)

        Write-Output "Accepting the proposal $proposalId"
        az cleanroom governance proposal vote `
            --proposal-id $proposalId `
            --action accept `
            --governance-client $governanceClient | jq

        $issuerInfo = Get-Oidc-Issuer `
            -useFrontendService $useFrontendService `
            -frontendServiceEndpoint $frontendServiceEndpoint `
            -governanceClient $governanceClient `
            -oidcIssuerLevel $oidcIssuerLevel
        $issuerUrl = $issuerInfo.issuerUrl

        Confirm-IssuerUrl `
            -expectedIssuerUrl $expectedIssuerUrl `
            -actualIssuerUrl $issuerUrl
    }
}
elseif ($oidcIssuerLevel -eq "user") {
    # User mode: Set issuer URL for the user's tenant using JWT authentication.
    $currentUser = (az account show) | ConvertFrom-Json
    $tenantId = $currentUser.tenantid

    $issuerInfo = Get-Oidc-Issuer `
        -useFrontendService $useFrontendService `
        -frontendServiceEndpoint $frontendServiceEndpoint `
        -governanceClient $governanceClient `
        -oidcIssuerLevel $oidcIssuerLevel

    # When using local IDP (for tests), the tenant ID in the JWT may come from a local IDP
    # and won't match `az account show`, so we only check if issuerInfo has data.
    if ($null -ne $issuerInfo -and $null -ne $issuerInfo.issuerUrl) {
        $displayTenantId = if ($issuerInfo.tenantId) { $issuerInfo.tenantId } else { $tenantId }
        Write-Host -ForegroundColor Yellow "OIDC issuer already set for user's tenant $displayTenantId."
        $issuerUrl = $issuerInfo.issuerUrl
    }
    else {
        Write-Host "Setting up OIDC issuer for user's tenant $tenantId (JWT auth)"

        Setup-OIDC-Issuer-StorageAccount `
            -resourceGroup $resourceGroup `
            -tenantId $tenantId `
            -outDir $outDir `
            -OIDC_STORAGE_ACCOUNT_NAME $OIDC_STORAGE_ACCOUNT_NAME `
            -OIDC_CONTAINER_NAME $OIDC_CONTAINER_NAME `
            -governanceClient $governanceClient `
            -useFrontendService $useFrontendService `
            -frontendServiceEndpoint $frontendServiceEndpoint
        $webUrl = Get-Content $outDir/web-url.txt

        $expectedIssuerUrl = "$webUrl${OIDC_CONTAINER_NAME}"
        Set-Oidc-Issuer-Url `
            -useFrontendService $useFrontendService `
            -frontendServiceEndpoint $frontendServiceEndpoint `
            -governanceClient $governanceClient `
            -issuerUrl $expectedIssuerUrl

        $issuerInfo = Get-Oidc-Issuer `
            -useFrontendService $useFrontendService `
            -frontendServiceEndpoint $frontendServiceEndpoint `
            -governanceClient $governanceClient `
            -oidcIssuerLevel $oidcIssuerLevel
        $issuerUrl = $issuerInfo.issuerUrl

        Confirm-IssuerUrl `
            -expectedIssuerUrl $expectedIssuerUrl `
            -actualIssuerUrl $issuerUrl
    }
}
else {
    # member-tenant mode: Set issuer URL for the member's tenant using member certificate auth.
    $currentUser = (az account show) | ConvertFrom-Json
    $tenantId = $currentUser.tenantid

    $issuerInfo = Get-Oidc-Issuer `
        -useFrontendService $useFrontendService `
        -frontendServiceEndpoint $frontendServiceEndpoint `
        -governanceClient $governanceClient `
        -oidcIssuerLevel $oidcIssuerLevel

    # When using local IDP (for tests), the tenant ID in the JWT may come from a local IDP
    # and won't match `az account show`, so we only check if issuerInfo has data.
    if ($null -ne $issuerInfo -and $null -ne $issuerInfo.issuerUrl) {
        $displayTenantId = if ($issuerInfo.tenantId) { $issuerInfo.tenantId } else { $tenantId }
        Write-Host -ForegroundColor Yellow "OIDC issuer already set for tenant $displayTenantId."
        $issuerUrl = $issuerInfo.issuerUrl
    }
    else {
        Write-Host "Setting up OIDC issuer for the member's tenant $tenantId (member cert auth)"

        Setup-OIDC-Issuer-StorageAccount `
            -resourceGroup $resourceGroup `
            -tenantId $tenantId `
            -outDir $outDir `
            -OIDC_STORAGE_ACCOUNT_NAME $OIDC_STORAGE_ACCOUNT_NAME `
            -OIDC_CONTAINER_NAME $OIDC_CONTAINER_NAME `
            -governanceClient $governanceClient `
            -useFrontendService $useFrontendService `
            -frontendServiceEndpoint $frontendServiceEndpoint
        $webUrl = Get-Content $outDir/web-url.txt

        $expectedIssuerUrl = "$webUrl${OIDC_CONTAINER_NAME}"
        Set-Oidc-Issuer-Url `
            -useFrontendService $useFrontendService `
            -frontendServiceEndpoint $frontendServiceEndpoint `
            -governanceClient $governanceClient `
            -issuerUrl $expectedIssuerUrl

        $issuerInfo = Get-Oidc-Issuer `
            -useFrontendService $useFrontendService `
            -frontendServiceEndpoint $frontendServiceEndpoint `
            -governanceClient $governanceClient `
            -oidcIssuerLevel $oidcIssuerLevel
        $issuerUrl = $issuerInfo.issuerUrl

        Confirm-IssuerUrl `
            -expectedIssuerUrl $expectedIssuerUrl `
            -actualIssuerUrl $issuerUrl
    }
}

Write-Output $issuerUrl > $outDir/issuer-url.txt
