param(
    [Parameter(Mandatory = $true)]
    [string]$resourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$outDir,

    [string]$backupKv = "",

    [string]$overridesFilePath = "",

    [string]$location = "westeurope"
)

$ErrorActionPreference = 'Stop'

# https://learn.microsoft.com/en-us/archive/blogs/389thoughts/get-uniquestring-generate-unique-id-for-azure-deployments
function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

$uniqueString = Get-UniqueString("${resourceGroup}")
$tdpKvName = "${uniqueString}kv"
$tdpmhsmName = "${uniqueString}mhsm"
$memberName = "$resourceGroup-member"

if ($overridesFilePath -ne "") {
    $overrides = Get-Content $overridesFilePath | Out-String | ConvertFrom-StringData
}
else {
    $overrides = @{}
}

mkdir -p $outDir

@"
`$RESOURCE_GROUP_LOCATION = $($overrides['$RESOURCE_GROUP_LOCATION'] ?? "`"$location`"")
`$STORAGE_ACCOUNT_NAME = $($overrides['$STORAGE_ACCOUNT_NAME'] ?? "`"${uniqueString}sa`"")
`$HSM_RESOURCE_GROUP = $($overrides['$HSM_RESOURCE_GROUP'] ?? "`"$resourceGroup`"")
`$MHSM_NAME = $($overrides['$MHSM_NAME'] ?? "`"$tdpmhsmName`"")
`$MAA_URL = $($overrides['$MAA_URL'] ?? "`"https://sharedneu.neu.attest.azure.net`"")
`$OUTPUT_CONTAINER_NAME = $($overrides['$OUTPUT_CONTAINER_NAME'] ?? "`"output`"")
`$MODEL_CONTAINER_NAME = $($overrides['$MODEL_CONTAINER_NAME'] ?? "`"model`"")
`$CONFIG_CONTAINER_NAME = $($overrides['$CONFIG_CONTAINER_NAME'] ?? "`"config`"")
`$INFRA_TELEMETRY_CONTAINER_NAME = $($overrides['$INFRA_TELEMETRY_CONTAINER_NAME'] ?? "`"infrastructure`"")
`$APP_TELEMETRY_CONTAINER_NAME = $($overrides['$APP_TELEMETRY_CONTAINER_NAME'] ?? "`"application`"")
`$KEYVAULT_NAME = $($overrides['$KEYVAULT_NAME'] ?? "`"$tdpKvName`"")
`$KEK_NAME = $($overrides['$KEK_NAME'] ?? "`"tdckek`"")
`$BACKUP_KEYVAULT_NAME = $($overrides['$BACKUP_KEYVAULT_NAME'] ?? "`"$backupKv`"")
`$MANAGED_IDENTITY_NAME = $($overrides['$MANAGED_IDENTITY_NAME'] ?? "`"${uniqueString}-mi`"")
`$OIDC_STORAGE_ACCOUNT_NAME = $($overrides['$OIDC_STORAGE_ACCOUNT_NAME'] ?? "`"${uniqueString}oidcsa`"")
`$OIDC_CONTAINER_NAME = $($overrides['$OIDC_CONTAINER_NAME'] ?? "`"$resourceGroup`"")
`$TELEMETRY_STORAGE_ACCOUNT_NAME = $($overrides['$TELEMETRY_STORAGE_ACCOUNT_NAME'] ?? "`"${uniqueString}logsa`"")
`$MODEL_WRAPPED_DEK_SECRET_NAME = $($overrides['$MODEL_WRAPPED_DEK_SECRET_NAME'] ?? "`"wrappeddek-model`"")
`$CONFIG_WRAPPED_DEK_SECRET_NAME = $($overrides['$CONFIG_WRAPPED_DEK_SECRET_NAME'] ?? "`"wrappeddek-config`"")
`$OUTPUT_WRAPPED_DEK_SECRET_NAME = $($overrides['$OUTPUT_WRAPPED_DEK_SECRET_NAME'] ?? "`"wrappeddek-output`"")
`$INFRA_TELEMETRY_WRAPPED_DEK_SECRET_NAME = $($overrides['$INFRA_TELEMETRY_WRAPPED_DEK_SECRET_NAME'] ?? "`"wrappeddek-infrastructure`"")
`$APP_TELEMETRY_DEK_SECRET_NAME = $($overrides['$APP_TELEMETRY_DEK_SECRET_NAME'] ?? "`"wrappeddek-application`"")
`$MEMBER_NAME = $($overrides['$MEMBER_NAME'] ?? "`"$memberName`"")
`$ENTERPRISE_APP_NAME = $($overrides['$ENTERPRISE_APP_NAME'] ?? "`"$uniqueString-app`"")
"@ > $outDir/names.generated.ps1
