function Deploy-Aci-Governance {
    [CmdletBinding()]
    param
    (
        [switch]
        $NoBuild,
        [Parameter(Mandatory)]
        [string]$resourceGroup,

        [Parameter(Mandatory)]
        [string]$location,

        [Parameter(Mandatory)]
        [string]$ccfName,

        [Parameter(Mandatory)]
        [string]$repo,

        [Parameter(Mandatory)]
        [string]$tag,

        [switch]$allowAll,

        [Parameter(Mandatory)]
        [string]$projectName,

        [Parameter(Mandatory)]
        [string]$initialMemberName,

        [Parameter(Mandatory)]
        [string]$outDir,
    
        [ValidateSet('mcr', 'local', 'acr')]
        [string]$registry = "local"
    )

    #https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
    $ErrorActionPreference = 'Stop'
    $PSNativeCommandUseErrorActionPreference = $true

    $root = git rev-parse --show-toplevel
    $outDir = "$outDir/ccf"
    mkdir -p $outDir

    function Get-UniqueString ([string]$id, $length = 13) {
        $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
        -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
    }

    $ISV_RESOURCE_GROUP = $resourceGroup
    $CCF_NAME = $ccfName

    # Creating the initial member identity certificate to add into the consortium.
    if (!(Test-Path "$outDir/${initialMemberName}_cert.pem")) {
        bash $root/samples/governance/keygenerator.sh --name $initialMemberName --out $outDir
    }

    az container delete --name $CCF_NAME --resource-group $ISV_RESOURCE_GROUP -y

    if ($env:GITHUB_ACTIONS -ne "true") {
        if (!$NoBuild) {
            # Install az cli before deploying ccf so that we can invoke az cleanroom ccf.
            # For Github Actions flow its built and installed as part of the workflow.
            pwsh $root/build/build-azcliext-cleanroom.ps1
        }
    }

    pwsh $PSScriptRoot/ccf-up.ps1 `
        -resourceGroup $ISV_RESOURCE_GROUP `
        -ccfName $CCF_NAME `
        -location $location `
        -initialMemberName $initialMemberName `
        -memberCertPath "$outDir/${initialMemberName}_cert.pem" `
        -repo $repo `
        -tag $tag `
        -allowAll:$allowAll `
        -outDir $outDir

    $response = (az cleanroom ccf network show `
            --name $CCF_NAME `
            --provider-config $outDir/providerConfig.json | ConvertFrom-Json)
    $ccfEndpoint = $response.endpoint

    pwsh $root/samples/governance/azcli/deploy-cgs.ps1 `
        -initialMemberName $initialMemberName `
        -projectName $projectName `
        -outDir $outDir `
        -NoTest `
        -NoBuild:$NoBuild `
        -ccfEndpoint $ccfEndpoint `
        -registry $registry `
        -repo $repo `
        -tag $tag
    $result = @{
        ccfEndpoint = $ccfEndpoint
    }
    return $result
}