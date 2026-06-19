[CmdletBinding()]
param
(
    [string]
    [ValidateSet('virtual', 'aks')]
    $infraType = "aks",

    [string]
    $clusterName = "",

    [switch]
    $NoBuild,

    [switch]
    $enableObservability,

    [switch]
    $enableAnalytics,

    [switch]
    $enableKServeInferencing,

    [switch]
    $enableFlexNode,

    [int]
    $flexNodeCount = 1,

    [string]
    $flexNodeVmSize = "",

    [switch]
    $enableMonitoring,

    [string]
    [ValidateSet("cached", "cached-debug", "allow-all")]
    $securityPolicyCreationOption = "allow-all",

    [string]
    $resourceGroup = "",

    [string]
    $clusterProviderProjectName = "cleanroom-cluster-provider",

    [string]
    $location = "centralindia",

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

if ($infraType -eq "aks" -and ($repo -eq "" -or $repo.StartsWith("localhost"))) {
    Write-Host -ForegroundColor Red "-repo with an acr value must be specified for aks." `
        "To build and push containers to an acr do:`n" `
        "az acr login -n <youracrname>`n" `
        "./build/cleanroom-cluster/build-cleanroom-cluster-infra-containers.ps1 -repo <youracrname>.azurecr.io -tag 1212 -push`n" `
        "./build/workloads/<workload-folder>/build-workload-infra-containers.ps1 -repo <youracrname>.azurecr.io -tag 1212 -push`n" `
        "./build/workloads/frontend/build-frontend-infra-containers.ps1 -repo <youracrname>.azurecr.io -tag 1212 -push`n" `
        "./samples/workloads/azcli/deploy-cluster.ps1 -infraType aks -repo <youracrname>.azurecr.io -tag 1212 ...`n"
    exit 1
}

if ($registry -eq "local" -and $repo.EndsWith("azurecr.io")) {
    $registry = "acr"
}

if ($infraType -ne "virtual") {
    $subscriptionId = az account show --query "id" -o tsv
    $tenantId = az account show --query "tenantId" -o tsv
}

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
    mkdir -p $sandbox_common
}
else {
    $sandbox_common = $outDir
}

if ($registry -ne "mcr") {
    if ($registry -eq "local") {
        if (!$NoBuild) {
            pwsh $build/build-azcliext-cleanroom.ps1
        }
    }

    $script:installWhl = $false
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        az cleanroom -h 2>$null 1>$null
        if ($LASTEXITCODE -gt 0) {
            Write-Host -ForegroundColor Red "az cli cleanroom extension not found. Installing..."
            $script:installWhl = $true
        }
    }
    if ($script:installWhl) {
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

        if (!$NoBuild) {
            pwsh $build/cleanroom-cluster/build-cleanroom-cluster-infra-containers.ps1 -repo $repo -tag latest

            $pushPolicy = $securityPolicyCreationOption -ne "allow-all"
            pwsh $build/workloads/analytics/build-workload-infra-containers.ps1 -repo $repo -tag latest -push -pushPolicy:$pushPolicy
            pwsh $build/workloads/inferencing/build-workload-infra-containers.ps1 -repo $repo -tag latest -push -pushPolicy:$pushPolicy
            pwsh $build/workloads/frontend/build-frontend-service.ps1 -repo $repo -tag latest -push -pushPolicy:$pushPolicy
            pwsh $build/k8s-node/build-api-server-proxy.ps1
        }

        docker tag $repo/ccr-proxy:latest $repo/ccr-proxy:$localTag
        docker push $repo/ccr-proxy:$localTag
        docker tag $repo/ccr-governance:latest $repo/ccr-governance:$localTag
        docker push $repo/ccr-governance:$localTag
        docker tag $repo/ccr-governance-virtual:latest $repo/ccr-governance-virtual:$localTag
        docker push $repo/ccr-governance-virtual:$localTag
        docker tag $repo/local-skr:latest $repo/local-skr:$localTag
        docker push $repo/local-skr:$localTag
        docker tag $repo/skr:latest $repo/skr:$localTag
        docker push $repo/skr:$localTag
        docker tag $repo/otel-collector:latest $repo/otel-collector:$localTag
        docker push $repo/otel-collector:$localTag
        docker tag $repo/workloads/cleanroom-spark-analytics-agent:latest $repo/workloads/cleanroom-spark-analytics-agent:$localTag
        docker push $repo/workloads/cleanroom-spark-analytics-agent:$localTag
        docker tag $repo/workloads/cleanroom-spark-frontend:latest $repo/workloads/cleanroom-spark-frontend:$localTag
        docker push $repo/workloads/cleanroom-spark-frontend:$localTag
        docker tag $repo/workloads/cleanroom-spark-analytics-app:latest $repo/workloads/cleanroom-spark-analytics-app:$localTag
        docker push $repo/workloads/cleanroom-spark-analytics-app:$localTag
        docker tag $repo/workloads/kserve-inferencing-agent:latest $repo/workloads/kserve-inferencing-agent:$localTag
        docker push $repo/workloads/kserve-inferencing-agent:$localTag
        docker tag $repo/workloads/kserve-inferencing-frontend:latest $repo/workloads/kserve-inferencing-frontend:$localTag
        docker push $repo/workloads/kserve-inferencing-frontend:$localTag

        pwsh $build/k8s-node/build-api-server-proxy.ps1 -push -repo $repo -tag $localTag -skipBuild

        docker tag $repo/cleanroom-cluster/cleanroom-cluster-provider-client:latest $repo/cleanroom-cluster/cleanroom-cluster-provider-client:$localTag
        docker push $repo/cleanroom-cluster/cleanroom-cluster-provider-client:$localTag
    }
    else {
        $localTag = $tag
    }
}
$CL_CLUSTER_RESOURCE_GROUP_LOCATION = ""
$CL_CLUSTER_RESOURCE_GROUP = ""
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

$envVars = @{}
$hostSharedDir = "$PSScriptRoot/shared"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_HOST_SHARED_DIR"] = $hostSharedDir

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

    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE"] = "$repo/cleanroom-cluster/cleanroom-cluster-provider-client:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE"] = "$repo/ccr-proxy:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE"] = "$repo/otel-collector:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE"] = "$repo/local-skr:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE"] = "$repo/skr:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE"] = "$repo/ccr-governance-virtual:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE"] = "$repo/ccr-governance:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE"] = "$repo/workloads/cleanroom-spark-analytics-agent:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL"] = "$ociEndpoint/workloads/helm/cleanroom-spark-analytics-agent:$semanticVersion"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/cleanroom-spark-analytics-agent-security-policy:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE"] = "$repo/workloads/cleanroom-spark-frontend:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL"] = "$ociEndpoint/workloads/helm/cleanroom-spark-frontend:$semanticVersion"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/cleanroom-spark-frontend-security-policy:$localTag"
    $envVars["AZCLI_CLEANROOM_SPARK_FRONTEND_VERSIONS_DOCUMENT_URL"] = "$repo/versions/workloads/cleanroom-spark-frontend:$tag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE"] = "$repo/workloads/kserve-inferencing-agent:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL"] = "$ociEndpoint/workloads/helm/kserve-inferencing-agent:$semanticVersion"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_SECURITY_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/kserve-inferencing-agent-security-policy:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE"] = "$repo/workloads/kserve-inferencing-frontend:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL"] = "$ociEndpoint/workloads/helm/kserve-inferencing-frontend:$semanticVersion"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/kserve-inferencing-frontend-security-policy:$localTag"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_API_SERVER_PROXY_PACKAGE_URL"] = "$ociEndpoint/k8s-node/api-server-proxy:$localTag"

    # Analytics App specific
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL"] = "$repo"
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP"] = $repo -eq "localhost:5000" ? "true" : "false"
    $envVars["AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL"] = "$repo/workloads/cleanroom-spark-analytics-app:$localTag"

    if ($repo -eq "localhost:5000") {
        # localhost:5000 is not reachable from the spark-frontend pod, so we need to use ccr-registry:5000.
        $podReachableRepo = "ccr-registry:5000"
    }
    else {
        $podReachableRepo = $repo
    }
    $envVars["AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL"] = "$podReachableRepo"
    $envVars["AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL"] = "$podReachableRepo/policies/workloads/cleanroom-spark-analytics-app-security-policy:$localTag"
    $envVars["AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL"] = "$podReachableRepo/sidecar-digests:$tag"
    $envVars["AZCLI_CLEANROOM_CVM_MEASUREMENTS_DOCUMENT_URL"] = "$podReachableRepo/cvm-measurements:$tag"
    $envVars["AZCLI_CLEANROOM_RUNTIME_DIGESTS_DOCUMENT_URL"] = "$podReachableRepo/inf-runtime-digests:$tag"
}
else {
    # Empty values so that default azurecr.io paths baked in the AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE get used.
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_SECURITY_POLICY_DOCUMENT_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_API_SERVER_PROXY_PACKAGE_URL"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP"] = ""
    $envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL"] = ""
    $envVars["AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL"] = ""
    $envVars["AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL"] = ""
    $envVars["AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL"] = ""
}

# Write environment variables to file
$envFilePath = "$sandbox_common/cluster-provider.env"
$envFileContent = $envVars.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
$envFileContent | Out-File -FilePath $envFilePath -Encoding utf8

az cleanroom cluster provider deploy --name $clusterProviderProjectName --env-file $envFilePath
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
if ($infraType -eq "aks") {
    $providerConfig.location = $CL_CLUSTER_RESOURCE_GROUP_LOCATION
    $providerConfig.subscriptionId = $subscriptionId
    $providerConfig.resourceGroupName = $CL_CLUSTER_RESOURCE_GROUP
    $providerConfig.tenantId = $tenantId
}

$providerConfig | ConvertTo-Json -Depth 100 > $sandbox_common/providerConfig.json

Write-Output "Creating $infraType clean room cluster named '$clusterName' with enable-analytics-workload: $($enableAnalytics.IsPresent), enable-kserve-inferencing-workload: $($enableKServeInferencing.IsPresent)"

# Build the cluster create command dynamically
$clusterCreateCmd = @(
    "az", "cleanroom", "cluster", "create",
    "--name", $clusterName,
    "--infra-type", $infraType,
    "--provider-config", "$sandbox_common/providerConfig.json",
    "--provider-client", $clusterProviderProjectName
)

if ($enableObservability) {
    $clusterCreateCmd += @(
        "--enable-observability"
    )
}

if ($enableMonitoring) {
    $clusterCreateCmd += @(
        "--enable-monitoring"
    )

    if ($infraType -eq "virtual") {
        $imagePathDir = "$hostSharedDir/images"
        mkdir -p $imagePathDir
        $imageTarFile = "$imagePathDir/llama3.1:8b-instruct.tar"
        if (-Not (Test-Path $imageTarFile)) {
            $image = "ghcr.io/kaito-project/aikit/llama3.1:8b-instruct"
            Write-Host "Pulling $image to save as tar for loading into the kind cluster..."
            docker pull $image
            Write-Host "Saving image to $imageTarFile for loading into the kind node"
            docker save $image --platform linux/amd64 > $imageTarFile
        }
        else {
            Write-Host "Found existing $imageTarFile so skipping docker pull/save."
        }
        # # ggml-model-Q4_K.gguf corresponds to llama-3.1:8b-instruct model when using aikit/local ai.
        # # If llama-3.1:8b-instruct is changed to a different model, the downloaded model to create a local cache
        # # to speed up pod start time by avoiding downloading the model within the pod should be updated accordingly.
        # $modelPathDir = "$hostSharedDir/models"
        # mkdir -p $modelPathDir
        # $modelPath = "$modelPathDir/ggml-model-Q4_K.gguf"
        # if (-Not (Test-Path $modelPath)) {
        #     Write-Host "Downloading ggml-model-Q4_K.gguf model under /models..."
        #     $modelUrl = "https://huggingface.co/galatolo/cerbero-7b-gguf/resolve/main/ggml-model-Q4_K.gguf"
        #     Invoke-WebRequest -Uri $modelUrl -OutFile $modelPath
        # }
        # else {
        #     Write-Host "Found existing $modelPath so not downloading again."
        # }
    }
}

# Add analytics workload parameters if enabled
if ($enableAnalytics) {
    pwsh $PSScriptRoot/deploy-analytics-workload-config-endpoint.ps1 `
        -outDir $sandbox_common `
        -repo $repo `
        -tag $tag

    $analyticsConfigFile = "$sandbox_common/analytics-workload-config-endpoint.json"
    $analyticsConfigUrl = (Get-Content $analyticsConfigFile | ConvertFrom-Json).url
    $analyticsConfigUrlCaCert = (Get-Content $analyticsConfigFile | ConvertFrom-Json).caCert

    $clusterCreateCmd += @(
        "--enable-analytics-workload",
        "--analytics-workload-config-url", $analyticsConfigUrl,
        "--analytics-workload-config-url-ca-cert", $analyticsConfigUrlCaCert,
        "--analytics-workload-security-policy-creation-option", $securityPolicyCreationOption
    )
}

# Add inferencing workload parameters if enabled
if ($enableKServeInferencing) {
    pwsh $PSScriptRoot/deploy-kserve-inferencing-workload-config-endpoint.ps1 `
        -outDir $sandbox_common `
        -repo $repo `
        -tag $tag

    $inferencingConfigFile = "$sandbox_common/kserve-inferencing-workload-config-endpoint.json"
    $inferencingConfigUrl = (Get-Content $inferencingConfigFile | ConvertFrom-Json).url
    $inferencingConfigUrlCaCert = (Get-Content $inferencingConfigFile | ConvertFrom-Json).caCert

    $clusterCreateCmd += @(
        "--enable-kserve-inferencing-workload",
        "--kserve-inferencing-workload-config-url", $inferencingConfigUrl,
        "--kserve-inferencing-workload-config-url-ca-cert", $inferencingConfigUrlCaCert,
        "--kserve-inferencing-workload-security-policy-creation-option", $securityPolicyCreationOption
    )
}

# Add flex node parameters if enabled
if ($enableFlexNode) {
    # Generate signing keys for api-server-proxy pod policy verification.
    pwsh $PSScriptRoot/generate-signing-keys.ps1 `
        -outDir $sandbox_common

    $signingConfigFile = "$sandbox_common/signing-config.json"
    $policySigningCertPath = (Get-Content $signingConfigFile | ConvertFrom-Json).policySigningCertPath

    # Generate SSH key pair for flex node VM access if it doesn't exist (only for non-virtual infra).
    $sshPrivateKeyPath = ""
    $sshPublicKeyPath = ""
    if ($infraType -ne "virtual") {
        $sshPrivateKeyPath = "$sandbox_common/flex-node-ssh-key.pem"
        $sshPublicKeyPath = "$sandbox_common/flex-node-ssh-key.pub"

        if (-Not (Test-Path $sshPrivateKeyPath)) {
            # Note: SSH key generation logic left as reference.
            # Write-Host "Generating SSH key pair for flex node VM access..."
            # ssh-keygen -t rsa -b 4096 -f "$sandbox_common/flex-node-ssh-key" -N "" -q
            # Move-Item "$sandbox_common/flex-node-ssh-key" $sshPrivateKeyPath
            # chmod 600 $sshPrivateKeyPath
            # Write-Host "SSH key pair generated at: $sshPrivateKeyPath"

            Write-Host "Downloading SSH keys from Key Vault 'azcleanroomemukv'..."
            $privateKeyContent = az keyvault secret show `
                --vault-name "azcleanroomemukv" `
                --name "flex-node-ssh-private-key" `
                --query "value" -o tsv
            $privateKeyContent | Out-File -FilePath $sshPrivateKeyPath
            chmod 600 $sshPrivateKeyPath

            $publicKeyContent = az keyvault secret show `
                --vault-name "azcleanroomemukv" `
                --name "flex-node-ssh-public-key" `
                --query "value" -o tsv
            $publicKeyContent | Out-File -FilePath $sshPublicKeyPath

            Write-Host "SSH keys downloaded from Key Vault."
        }
        else {
            Write-Host "Found existing SSH key pair at: $sshPrivateKeyPath"
        }
    }
    else {
        Write-Host "Skipping SSH key pair generation for virtual infra type."
    }

    $clusterCreateCmd += @(
        "--enable-flex-node",
        "--flex-node-policy-signing-cert", $policySigningCertPath,
        "--flex-node-count", $flexNodeCount
    )

    if ($flexNodeVmSize -ne "") {
        $clusterCreateCmd += @("--flex-node-vm-size", $flexNodeVmSize)
    }

    if ($infraType -ne "virtual") {
        $clusterCreateCmd += @(
            "--flex-node-ssh-private-key", $sshPrivateKeyPath,
            "--flex-node-ssh-public-key", $sshPublicKeyPath
        )
    }
}

# Execute the cluster create command
& $clusterCreateCmd[0] $clusterCreateCmd[1..($clusterCreateCmd.Length - 1)]

# Wait for analytics endpoint if enabled
if ($enableAnalytics) {
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

# Wait for inferencing endpoint if enabled
if ($enableKServeInferencing) {
    $timeout = New-TimeSpan -Minutes 10
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $inferencingEndpoint = az cleanroom cluster show `
            --name $clusterName `
            --infra-type $infraType `
            --query "inferencingWorkloadProfile.kserveProfile.endpoint" `
            --output tsv `
            --provider-config $sandbox_common/providerConfig.json `
            --provider-client $clusterProviderProjectName
        if (![string]::IsNullOrEmpty($inferencingEndpoint)) {
            Write-Host "Inferencing endpoint is up at: $inferencingEndpoint"
            break
        }

        if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for inferencing endpoint to become available."
        }

        Write-Host "Waiting for inferencing endpoint to be up..."
        Start-Sleep -Seconds 5
    }
}

$response = az cleanroom cluster show `
    --name $clusterName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json `
    --provider-client $clusterProviderProjectName

$response | Out-File $sandbox_common/cl-cluster.json

Write-Output "Cluster health:"
az cleanroom cluster show-health `
    --name $clusterName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json `
    --provider-client $clusterProviderProjectName

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