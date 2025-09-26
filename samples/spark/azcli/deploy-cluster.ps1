[CmdletBinding()]
param
(
    [string]
    [ValidateSet('virtual', 'caci')]
    $infraType = "caci",

    [string]
    $clusterName = "",

    [switch]
    $NoBuild,

    [switch]
    $donotEnableAnalytics,

    [string]
    [ValidateSet("cached", "cached-debug", "allow-all")]
    $securityPolicyCreationOption = "allow-all",

    [string]
    $resourceGroup = "",

    [string]
    $clusterProviderProjectName = "cleanroom-cluster-provider",

    [string]
    $location = "westeurope",

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [string]
    $outDir = ""
)

function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"

. $root/build/helpers.ps1
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

if ($infraType -eq "caci" -and ($repo -eq "" -or $repo.StartsWith("localhost"))) {
    Write-Host -ForegroundColor Red "-repo with an acr value must be specified for caci." `
        "To build and push containers to an acr do:`n" `
        "az acr login -n <youracrname>`n" `
        "./build/ccf/build-cleanroom-cluster-infra-containers.ps1 -repo <youracrname>.azurecr.io -tag 1212 -push`n" `
        "./build/ccf/build-workload-infra-containers.ps1 -repo <youracrname>.azurecr.io -tag 1212 -push`n" `
        "./samples/spark/azcli/deploy-cluster.ps1 -infraType caci -repo <youracrname>.azurecr.io -tag 1212 ...`n"
    exit 1
}

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
    mkdir -p $sandbox_common
}
else {
    $sandbox_common = $outDir
}

if ($registry -eq "acr") {
    $whlPath = "$repo/cli/cleanroom-whl:$tag"
    Write-Host "Downloading and installing az cleanroom cli from ${whlPath}"
    if ($env:GITHUB_ACTIONS -eq "true") {
        oras pull $whlPath --output $sandbox_common
    }
    else {
        $orasImage = "ghcr.io/oras-project/oras:v1.2.0"
        docker run --rm --network host -v ${sandbox_common}:/workspace -w /workspace `
            $orasImage pull $whlPath
    }

    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        az extension remove --name cleanroom 2>$null
    }
    az extension add `
        --allow-preview true `
        --source ${sandbox_common}/cleanroom-*-py2.py3-none-any.whl -y
}
elseif (!$NoBuild) {
    pwsh $build/build-azcliext-cleanroom.ps1
}

if ($registry -ne "mcr") {
    if ($registry -eq "local") {
        # Create registry container unless it already exists.
        $reg_name = "ccr-registry"
        $reg_port = "5000"
        $registryImage = "registry:2.7"
        if ($env:GITHUB_ACTIONS -eq "true") {
            $registryImage = "cleanroombuild.azurecr.io/registry:2.7"
        }

        & {
            # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
            $PSNativeCommandUseErrorActionPreference = $false
            $registryState = docker inspect -f '{{.State.Running}}' "${reg_name}" 2>$null
            if ($registryState -ne "true") {
                docker run -d --restart=always -p "127.0.0.1:${reg_port}:5000" --network bridge --name "${reg_name}" $registryImage
            }
        }

        $localTag = "100.$(Get-Date -UFormat %s)"
        $localTag | Out-File $sandbox_common/local-registry-tag.txt

        if (!$NoBuild) {
            pwsh $build/cleanroom-cluster/build-cleanroom-cluster-infra-containers.ps1 -repo $repo -tag latest

            $pushPolicy = $securityPolicyCreationOption -ne "allow-all"
            pwsh $build/workloads/build-workload-infra-containers.ps1 -repo $repo -tag latest -push -pushPolicy:$pushPolicy
        }

        docker tag $repo/ccr-proxy:latest $repo/ccr-proxy:$localTag
        docker push $repo/ccr-proxy:$localTag
        docker tag $repo/ccr-governance:latest $repo/ccr-governance:$localTag
        docker push $repo/ccr-governance:$localTag
        docker tag $repo/ccr-governance-virtual:latest $repo/ccr-governance-virtual:$localTag
        docker push $repo/ccr-governance-virtual:$localTag
        docker tag $repo/local-skr:latest $repo/local-skr:$localTag
        docker push $repo/local-skr:$localTag
        docker tag $repo/ccr-attestation:latest $repo/ccr-attestation:$localTag
        docker push $repo/ccr-attestation:$localTag
        docker tag $repo/otel-collector:latest $repo/otel-collector:$localTag
        docker push $repo/otel-collector:$localTag
        docker tag $repo/workloads/cleanroom-spark-analytics-agent:latest $repo/workloads/cleanroom-spark-analytics-agent:$localTag
        docker push $repo/workloads/cleanroom-spark-analytics-agent:$localTag
        docker tag $repo/workloads/cleanroom-spark-frontend:latest $repo/workloads/cleanroom-spark-frontend:$localTag
        docker push $repo/workloads/cleanroom-spark-frontend:$localTag
        docker tag $repo/workloads/cleanroom-spark-analytics-app:latest $repo/workloads/cleanroom-spark-analytics-app:$localTag
        docker push $repo/workloads/cleanroom-spark-analytics-app:$localTag

        docker tag $repo/cleanroom-cluster/cleanroom-cluster-provider-client:latest $repo/cleanroom-cluster/cleanroom-cluster-provider-client:$localTag
        docker push $repo/cleanroom-cluster/cleanroom-cluster-provider-client:$localTag
    }
    else {
        $localTag = $tag
    }
}
$CL_CLUSTER_RESOURCE_GROUP_LOCATION = ""
$CL_CLUSTER_RESOURCE_GROUP = ""
$subscriptionId = az account show --query "id" -o tsv
$tenantId = az account show --query "tenantId" -o tsv
$resourceGroupTags = ""
if ($resourceGroup -ne "") {
    $CL_CLUSTER_RESOURCE_GROUP = $resourceGroup
}
else {
    if ($env:GITHUB_ACTIONS -eq "true") {
        $uniqueString = Get-UniqueString("cleanroom-cluster-${env:JOB_ID}-${env:RUN_ID}")
        $CL_CLUSTER_RESOURCE_GROUP = "rg-${uniqueString}"
        $resourceGroupTags = "github_actions=cleanroom-cluster-${env:JOB_ID}-${env:RUN_ID}"
    }
    else {
        $CL_CLUSTER_RESOURCE_GROUP = "cleanroom-cluster-ob-${env:USER}"
    }
}

$CL_CLUSTER_RESOURCE_GROUP_LOCATION = $location

if ($infraType -ne "virtual") {
    $subscriptionId = az account show --query "id" -o tsv
    Write-Output "Creating resource group $CL_CLUSTER_RESOURCE_GROUP in $CL_CLUSTER_RESOURCE_GROUP_LOCATION"
    az group create `
        --location $CL_CLUSTER_RESOURCE_GROUP_LOCATION `
        --name $CL_CLUSTER_RESOURCE_GROUP `
        --tags $resourceGroupTags 1>$null
}

if ($env:GITHUB_ACTIONS -eq "true") {
    if ($clusterName -eq "") {
        $uniqueString = Get-UniqueString("cleanroom-cluster-${env:JOB_ID}-${env:RUN_ID}")
        $clusterName = "cleanroom-cluster-${uniqueString}"
    }
}
else {
    if ($clusterName -eq "") {
        if ($infraType -eq "virtual") {
            $clusterName = "testcluster-virtual"
        }
        else {
            $uniqueString = Get-UniqueString("${CL_CLUSTER_RESOURCE_GROUP}")
            $clusterName = "cleanroom-cluster-${uniqueString}"
        }
    }
}

if ($registry -ne "mcr") {
    $ociEndpoint = $repo
    if ($repo.StartsWith("localhost:5000")) {
        if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
            $ociEndpoint = "host.docker.internal:5000"
        }
        elseif ($env:CODESPACES -eq "true") {
            # 172.17.0.1:5000 is not reachable from the cleanroom-cluster-provider-client-1 container, so we need to use ccr-registry:5000.
            $ociEndpoint = "ccr-registry:5000"
        }
        else {
            # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
            $ociEndpoint = "172.17.0.1:5000"
        }
    }
    $semanticVersion = Get-SemanticVersionFromTag $tag

    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE = "$repo/cleanroom-cluster/cleanroom-cluster-provider-client:$localTag"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE = "$repo/ccr-proxy:$localTag"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_ATTESTATION_IMAGE = "$repo/ccr-attestation:$localTag"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE = "$repo/otel-collector:$localTag"
    if ($infraType -eq "virtual") {
        $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE = "$repo/ccr-governance-virtual:$localTag"
        $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE = "$repo/local-skr:$localTag"
    }
    else {
        $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE = "$repo/ccr-governance:$localTag"
        $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE = "$repo/skr:$localTag"
    }
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE = "$repo/workloads/cleanroom-spark-analytics-agent:$localTag"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL = "$ociEndpoint/workloads/helm/cleanroom-spark-analytics-agent:$semanticVersion"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL = "$repo/policies/workloads/cleanroom-spark-analytics-agent-security-policy:$localTag"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE = "$repo/workloads/cleanroom-spark-frontend:$localTag"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL = "$ociEndpoint/workloads/helm/cleanroom-spark-frontend:$semanticVersion"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL = "$repo/policies/workloads/cleanroom-spark-frontend-security-policy:$localTag"
    $env:AZCLI_CLEANROOM_SPARK_FRONTEND_VERSIONS_DOCUMENT_URL = "$repo/versions/workloads/cleanroom-spark-frontend:$tag"

    # Analytics App specific
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL = "$repo"
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP = $repo -eq "localhost:5000" ? "true" : "false"
    $env:AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL = "$repo/workloads/cleanroom-spark-analytics-app:$localTag"

    if ($repo -eq "localhost:5000") {
        # localhost:5000 is not reachable from the spark-frontend pod, so we need to use ccr-registry:5000.
        $podReachableRepo = "ccr-registry:5000"
    }
    else {
        $podReachableRepo = $repo
    }
    $env:AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL = "$podReachableRepo"
    $env:AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL = "$podReachableRepo/policies/workloads/cleanroom-spark-analytics-app-security-policy:$localTag"
    $env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = "$podReachableRepo/sidecar-digests:$tag"
}
else {
    # Unset these so that default azurecr.io paths baked in the AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE get used.
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_ATTESTATION_IMAGE = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP = ""
    $env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL = ""
    $env:AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL = ""
    $env:AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL = ""
    $env:AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL = ""
}
az cleanroom cluster provider deploy --name $clusterProviderProjectName
if ($env:CODESPACES -eq "true") {
    # 172.17.0.1:5000 is not reachable from the cleanroom-cluster-provider-client-1 container, 
    # so need to connect the registry and the client container on the same network so that 'ccr-registry:5000'
    # is reachable.
    $networkName = "${clusterProviderProjectName}_default"
    $ccrRegistryName = "ccr-registry"
    $alreadyConnected = docker inspect $ccrRegistryName | jq ".[0].NetworkSettings.Networks | has(`"$networkName`")"
    if ($alreadyConnected -ne "true") {
        docker network connect $networkName $ccrRegistryName
    }
}

$providerConfig = @{}
if ($infraType -eq "caci") {
    $providerConfig.location = $CL_CLUSTER_RESOURCE_GROUP_LOCATION
    $providerConfig.subscriptionId = $subscriptionId
    $providerConfig.resourceGroupName = $CL_CLUSTER_RESOURCE_GROUP
    $providerConfig.tenantId = $tenantId
}

$providerConfig | ConvertTo-Json -Depth 100 > $sandbox_common/providerConfig.json

Write-Output "Creating $infraType clean room cluster named '$clusterName' with enable-analytics-workload: $(!$donotEnableAnalytics.IsPresent)"
if ($donotEnableAnalytics) {
    az cleanroom cluster create `
        --name $clusterName `
        --infra-type $infraType `
        --enable-observability `
        --provider-config $sandbox_common/providerConfig.json `
        --provider-client $clusterProviderProjectName
}
else {
    pwsh $PSScriptRoot/deploy-analytics-workload-config-endpoint.ps1 `
        -outDir $sandbox_common `
        -repo $repo `
        -tag $tag

    $analyticsConfigFile = "$sandbox_common/analytics-workload-config-endpoint.json"
    $configUrl = (Get-Content $analyticsConfigFile | ConvertFrom-Json).url
    $configUrlCaCert = (Get-Content $analyticsConfigFile | ConvertFrom-Json).caCert

    az cleanroom cluster create `
        --name $clusterName `
        --infra-type $infraType `
        --enable-observability `
        --enable-analytics-workload `
        --analytics-workload-config-url $configUrl `
        --analytics-workload-config-url-ca-cert $configUrlCaCert `
        --analytics-workload-security-policy-creation-option $securityPolicyCreationOption `
        --provider-config $sandbox_common/providerConfig.json `
        --provider-client $clusterProviderProjectName

    $timeout = New-TimeSpan -Minutes 10
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $analyticsEndpoint = az cleanroom cluster show `
            --name $clusterName `
            --infra-type $infraType `
            --query "analyticsWorkloadProfile.endpoint" `
            --output tsv `
            --provider-config $sandbox_common/providerConfig.json `
            --provider-client $clusterProviderProjectName
        if (![string]::IsNullOrEmpty($analyticsEndpoint)) {
            Write-Host "Analytics endpoint is up at: $analyticsEndpoint"
            break
        }

        if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for analytics endpoint to become available."
        }

        Write-Host "Waiting for analytics endpoint to be up..."
        Start-Sleep -Seconds 5
    }
}

$response = az cleanroom cluster show `
    --name $clusterName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json `
    --provider-client $clusterProviderProjectName

$response | Out-File $sandbox_common/cl-cluster.json

Write-Host "Getting k8s credentials..."
$kubeConfig = "${sandbox_common}/k8s-credentials.yaml"
az cleanroom cluster get-kubeconfig `
    --name $clusterName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json `
    -f $kubeConfig `
    --provider-client $clusterProviderProjectName

@"
{
  "repo": "$repo",
  "tag": "$tag",
  "clusterProviderProjectName": "$clusterProviderProjectName"
}
"@ > $sandbox_common/repoConfig.json