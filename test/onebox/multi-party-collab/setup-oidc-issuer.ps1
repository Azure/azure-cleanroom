param(
  [Parameter(Mandatory = $true)]
  [string]$resourceGroup,
  [Parameter(Mandatory = $true)]
  [string]$governanceClient,
  [Parameter()]
  [ValidateSet("member-tenant", "global", "user")]
  [string]$oidcIssuerLevel = "member-tenant",
  [string]$outDir = ""
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
    [string]$governanceClient
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

  $ccfEndpoint = (az cleanroom governance client show --name $governanceClient | ConvertFrom-Json)
  $url = "$($ccfEndpoint.ccfEndpoint)/app/oidc/keys"
  curl -s -k $url | jq > $outDir/jwks.json

  az storage blob upload `
    --container-name '$web' `
    --file $outDir/jwks.json `
    --name ${OIDC_CONTAINER_NAME}/openid/v1/jwks `
    --account-name $storageAccountResult.name `
    --overwrite `
    --auth-mode login

  Write-Output $webUrl > $outDir/web-url.txt
}

# Set OIDC issuer.
if ($oidcIssuerLevel -eq "global" -or $oidcIssuerLevel -eq "user") {
  $issuerInfo = (az cleanroom governance oidc-issuer show `
      --governance-client $governanceClient | ConvertFrom-Json)
  if ($null -ne $issuerInfo.issuerUrl) {
    Write-Host -ForegroundColor Yellow "$oidcIssuerLevel level OIDC issuer already set, skipping."
    $issuerUrl = $issuerInfo.issuerUrl
  }
  else {
    Write-Host "Setting up $oidcIssuerLevel level OIDC issuer url"
    $currentUser = (az account show) | ConvertFrom-Json
    $tenantId = $currentUser.tenantid
    Setup-OIDC-Issuer-StorageAccount `
      -resourceGroup $resourceGroup `
      -tenantId $tenantId `
      -outDir $outDir `
      -OIDC_STORAGE_ACCOUNT_NAME $OIDC_STORAGE_ACCOUNT_NAME `
      -OIDC_CONTAINER_NAME $OIDC_CONTAINER_NAME `
      -governanceClient $governanceClient
    $webUrl = Get-Content $outDir/web-url.txt

    if ($oidcIssuerLevel -eq "global") {
      $proposalId = (az cleanroom governance oidc-issuer propose-set-issuer-url `
          --url "$webUrl${OIDC_CONTAINER_NAME}" `
          --governance-client $governanceClient `
          --query "proposalId" `
          --output tsv)

      Write-Output "Accepting the proposal $proposalId"
      az cleanroom governance proposal vote `
        --proposal-id $proposalId `
        --action accept `
        --governance-client $governanceClient | jq
      $issuerInfo = (az cleanroom governance oidc-issuer show `
          --governance-client $governanceClient | ConvertFrom-Json)
      $issuerUrl = $issuerInfo.issuerUrl
    }
    else {
      Write-Output "Not making any proposal for $oidcIssuerLevel level OIDC issuer url."
      $issuerUrl = "$webUrl${OIDC_CONTAINER_NAME}"
    }
  }
}
else {
  $currentUser = (az account show) | ConvertFrom-Json
  $tenantId = $currentUser.tenantid
  $tenantData = (az cleanroom governance oidc-issuer show `
      --governance-client $governanceClient `
      --query "tenantData" | ConvertFrom-Json)
  if ($null -ne $tenantData -and $tenantData.tenantId -eq $tenantId) {
    Write-Host -ForegroundColor Yellow "OIDC issuer already set for tenant $tenantId, skipping."
    $issuerUrl = $tenantData.issuerUrl
  }
  else {
    Write-Host "Setting up OIDC issuer for the member's tenant $tenantId"

    Setup-OIDC-Issuer-StorageAccount `
      -resourceGroup $resourceGroup `
      -tenantId $tenantId `
      -outDir $outDir `
      -OIDC_STORAGE_ACCOUNT_NAME $OIDC_STORAGE_ACCOUNT_NAME `
      -OIDC_CONTAINER_NAME $OIDC_CONTAINER_NAME `
      -governanceClient $governanceClient
    $webUrl = Get-Content $outDir/web-url.txt

    az cleanroom governance oidc-issuer set-issuer-url `
      --governance-client $governanceClient `
      --url "$webUrl${OIDC_CONTAINER_NAME}"
    $tenantData = (az cleanroom governance oidc-issuer show `
        --governance-client $governanceClient `
        --query "tenantData" | ConvertFrom-Json)
    $issuerUrl = $tenantData.issuerUrl
  }
}

Write-Output $issuerUrl > $outDir/issuer-url.txt

function Get-Assignee-Principal-Type {
  if ($env:GITHUB_ACTIONS -eq "true") {
    return "ServicePrincipal"
  }
  else {
    return "User"
  }
}