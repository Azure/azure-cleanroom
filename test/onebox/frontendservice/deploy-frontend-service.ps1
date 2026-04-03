param(
    [switch]
    $NoBuild,

    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [string]$frontendPort = "61001",

    [parameter(Mandatory = $false)]
    $outDir = "",

    [switch]
    $deployOnAKS = $false,

    [switch]
    $allowAll = $false
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

if (!$NoBuild) {
    pwsh $root/build/workloads/frontend/build-frontend-service.ps1 -repo $repo -tag $tag -push
}

if (!$deployOnAKS) {
    # Invoke docker-compose.yml
    $env:cgsClientImage = "$repo/cgs-client:$tag"
    $env:mockServerImage = "$repo/mock-server:$tag"
    $env:frontendServiceImage = "$repo/frontend-service:$tag"
    $env:frontendTenantId = az account show --query tenantId -o tsv
    if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
        $env:network = "http://host.docker.internal"
    }
    else {
        # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
        $env:network = "http://172.17.0.1"
    }

    $composeFile = "$PSScriptRoot/docker-compose.yml"
    Write-Host "Deploying frontend service containers using docker-compose file: $composeFile"
    docker compose -f $composeFile up -d
    if ($LASTEXITCODE -ne 0) {
        Write-Error "docker compose failed with error code: $LASTEXITCODE."
        exit 1
    }

    $sleepTime = 5
    Write-Host "Waiting for $sleepTime seconds for frontend-service to be up"
    Start-Sleep -Seconds $sleepTime

    curl --fail-with-body -sS -X GET http://localhost:${frontendPort}/ready -H "content-type: application/json" 1>$null
    Write-Host "Frontend status ready"

    curl --fail-with-body -sS -X GET http://localhost:${frontendPort}/checkConnectionsInit -H "content-type: application/json" 1>$null
    Write-Host "Frontend checkConnectionsInit successful"
}
else {
    Write-Host "Deploying Frontend Service to the AKS cluster" -ForegroundColor Green
    $semanticVersion = Get-SemanticVersionFromTag $tag
    $ns = "frontend-service"
    $chartPath = "oci://${repo}/workloads/helm/frontend-service:$semanticVersion" # acr path of the helm chart with version tag
    $release = "frontend-service"

    if ($allowAll) {
        Write-Host "Using allow all CCE policy for frontend service deployment" -ForegroundColor Yellow
        $ccePolicy = "cGFja2FnZSBwb2xpY3kKCmFwaV9zdm4gOj0gIjAuMTAuMCIKCm1vdW50X2RldmljZSA6PSB7ImFsbG93ZWQiOiB0cnVlfQptb3VudF9vdmVybGF5IDo9IHsiYWxsb3dlZCI6IHRydWV9CmNyZWF0ZV9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbCwgImFsbG93X3N0ZGlvX2FjY2VzcyI6IHRydWV9CnVubW91bnRfZGV2aWNlIDo9IHsiYWxsb3dlZCI6IHRydWV9IAp1bm1vdW50X292ZXJsYXkgOj0geyJhbGxvd2VkIjogdHJ1ZX0KZXhlY19pbl9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbH0KZXhlY19leHRlcm5hbCA6PSB7ImFsbG93ZWQiOiB0cnVlLCAiZW52X2xpc3QiOiBudWxsLCAiYWxsb3dfc3RkaW9fYWNjZXNzIjogdHJ1ZX0Kc2h1dGRvd25fY29udGFpbmVyIDo9IHsiYWxsb3dlZCI6IHRydWV9CnNpZ25hbF9jb250YWluZXJfcHJvY2VzcyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV9tb3VudCA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CmdldF9wcm9wZXJ0aWVzIDo9IHsiYWxsb3dlZCI6IHRydWV9CmR1bXBfc3RhY2tzIDo9IHsiYWxsb3dlZCI6IHRydWV9CnJ1bnRpbWVfbG9nZ2luZyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpsb2FkX2ZyYWdtZW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CnNjcmF0Y2hfbW91bnQgOj0geyJhbGxvd2VkIjogdHJ1ZX0Kc2NyYXRjaF91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cg=="
    }
    else {
        # Pull the policy document from registry
        $policyDir = Join-Path $outDir "policy-$(Get-Random)"
        New-Item -ItemType Directory -Path $policyDir -Force | Out-Null
        $policyPath = "${repo}/policies/workloads/frontend-service-security-policy:$tag"

        Write-Host "Pulling policy from $policyPath to temporary directory $policyDir"
        oras pull $policyPath --output $policyDir
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to pull policy from $policyPath"
            exit 1
        }
        $policyRego = Get-Content -Path "$policyDir/frontend-service-security-policy.yaml" | ConvertFrom-Yaml
        $ccePolicy = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyRego.rego_debug))
    }

    helm upgrade --install $release $chartPath `
        --namespace $ns --create-namespace `
        --set "image.frontendImage=$repo/frontend-service:$tag" `
        --set "cgsClient.image=$repo/cgs-client:$tag" `
        --set "ccrProxy.image=$repo/ccr-proxy:$tag" `
        --set "skr.image=$repo/skr:$tag" `
        --set "podAnnotations.microsoft\.containerinstance\.virtualnode\.ccepolicy=$ccePolicy" `
        --kubeconfig "${outDir}/cl-cluster/k8s-credentials.yaml"

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Helm deployment failed with error code: $LASTEXITCODE."
        exit 1
    }
    Write-Host "Frontend Service deployed successfully to AKS cluster in namespace $ns"
    Write-Host "Waiting for frontend-service pod to become ready..." -ForegroundColor Yellow

    $maxRetries = 30
    $retryInterval = 10
    $retryCount = 0

    do {
        $retryCount++
        Write-Host "Checking pod status (attempt $retryCount/$maxRetries)..."
        $podStatus = kubectl get pods -n $ns --kubeconfig "${outDir}/cl-cluster/k8s-credentials.yaml" -o jsonpath='{.items[0].status.phase}' 2>$null
        if ($LASTEXITCODE -ne 0) {
            $podStatus = ""
        }

        if ($podStatus -eq "Running") {
            # Check if pod is ready
            $readyStatus = kubectl get pods -n $ns --kubeconfig "${outDir}/cl-cluster/k8s-credentials.yaml" -o jsonpath='{.items[0].status.conditions[?(@.type=="Ready")].status}' 2>$null
            if ($LASTEXITCODE -ne 0) {
                $readyStatus = ""
            }
            if ($readyStatus -eq "True") {
                Write-Host "Frontend-service pod is running and ready!" -ForegroundColor Green
                break
            }
            else {
                Write-Host "Pod is running but not ready yet..."
            }
        }
        elseif ($podStatus -eq "Pending") {
            Write-Host "Pod is still pending..."
        }
        elseif ($podStatus -eq "Failed" -or $podStatus -eq "CrashLoopBackOff") {
            Write-Error "Pod failed to start. Status: $podStatus"
            kubectl describe pods -n $ns -l app=frontend-service --kubeconfig "${outDir}/cl-cluster/k8s-credentials.yaml"
            exit 1
        }
        else {
            Write-Host "Pod status: $podStatus"
        }

        if ($retryCount -lt $maxRetries) {
            Write-Host "Waiting $retryInterval seconds before next check..."
            Start-Sleep -Seconds $retryInterval
        }
    } while ($retryCount -lt $maxRetries)

    if ($retryCount -eq $maxRetries) {
        Write-Error "Timeout waiting for frontend-service pod to become ready"
        kubectl get pods -n $ns --kubeconfig "${outDir}/cl-cluster/k8s-credentials.yaml"
        kubectl describe pods -n $ns -l app=frontend-service --kubeconfig "${outDir}/cl-cluster/k8s-credentials.yaml"
        exit 1
    }
}

<#
# Use kubectl proxy to access the frontend-service via the apiserver proxy URL.
# kubectl port-forward does not work with virtual nodes (ACI), so we use kubectl proxy
# with the apiserver proxy URL instead.
# https://kubernetes.io/docs/tasks/access-application-cluster/access-cluster-services/#manually-constructing-apiserver-proxy-urls
$proxyPort = 8384
$kubeconfigPath = "${outDir}/cl-cluster/k8s-credentials.yaml"
Get-Job -Command "*kubectl proxy --port $proxyPort*" | Stop-Job
Get-Job -Command "*kubectl proxy --port $proxyPort*" | Remove-Job
kubectl proxy --port $proxyPort --kubeconfig $kubeconfigPath &
$serviceAddress = "http://localhost:${proxyPort}/api/v1/namespaces/${ns}/services/https:${release}:443/proxy"

Start-Sleep -Seconds 5

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$timeout = New-TimeSpan -Minutes 2
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock.
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -o /dev/null -w "%{http_code}" -k -s ${serviceAddress}/ready) -ne "200") {
        Write-Host "Waiting for frontend endpoint to be ready at ${serviceAddress}/ready"
        Start-Sleep -Seconds 3
        if ($stopwatch.elapsed -gt $timeout) {
            curl -k -s ${serviceAddress}/ready
            throw "Hit timeout waiting for frontend endpoint to be ready."
        }
    }
}
$readyResponse = curl -k -s ${serviceAddress}/ready
Write-Host "Frontend status ready. Response: $readyResponse"

Get-Job -Command "*kubectl proxy --port $proxyPort*" | Stop-Job
Get-Job -Command "*kubectl proxy --port $proxyPort*" | Remove-Job

$ccfConfig = Get-Content -Path "$outDir/ccf/ccf.json" -Raw | ConvertFrom-Json
$ccfEndpoint = $ccfConfig.Endpoint
Write-Host "CCF Endpoint: $ccfEndpoint"

$serviceCertPem = Get-Content -Path "$outDir/ccf/service_cert.pem" -Raw

write-Host "Getting user token for CGS call"
$userToken = (az cleanroom governance client get-access-token --query accessToken -o tsv --name "ob-cr-consumer-user-client-cgs-client-1")

$consortiumDetails = @{
    "ccfEndpoint"       = "ccf-test1"
    "ccfServiceCertPem" = "ccf-pem1"
} | ConvertTo-Json -Depth 10

write-Host "\nAdding collaboration details to mock membership manager..."
curl --fail-with-body -sS -X POST http://localhost:61003/collaborations/collab1 `
    -H "content-type: application/json" `
    -d $consortiumDetails

write-Host "\nGetting collaboration details from mock membership manager..."
curl --fail-with-body -sS -X GET http://localhost:61001/collaborations/collab1?api-version=2026-03-01-preview `
    -H "content-type: application/json" `
    -H "Authorization: Bearer $userToken"


Write-Host "Getting all collaborations from mock membership manager..."
curl --fail-with-body -sS -X GET http://localhost:61001/collaborations?api-version=2026-03-01-preview `
    -H "content-type: application/json" `
    -H "Authorization: Bearer $userToken"


$userToken = "mock-token-12345"
$header = '{"alg":"none","typ":"JWT"}'
$payload = @"
{
    "oid": "12345678-1234-1234-1234-123456789abc",
    "tid": "87654321-4321-4321-4321-cba987654321",
    "preferred_username": "testuser@example.com"
}
"@
$headerEncoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($header)) -replace '\+','-' -replace '/','_' -replace '='
$payloadEncoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($payload)) -replace '\+','-' -replace '/','_' -replace '='
$userToken = "$headerEncoded.$payloadEncoded."
write-Host "Get collab details from frontend service..."
curl --fail-with-body -sS -X GET http://localhost:61001/collaborations/collab1?api-version=2026-03-01-preview `
    -H "content-type: application/json" `
    -H "Authorization: Bearer $userToken"

write-Host "Doing CGS call to list contracts"
$return = curl --fail-with-body -X GET http://localhost:61001/collaborations/collab1/contracts?api-version=2026-03-01-preview `
    -H "content-type: application/json" `
    -H "Authorization: Bearer $userToken"

if ($LASTEXITCODE -ne 0) {
    Write-Error "curl command failed with exit code: $LASTEXITCODE"
    exit 1
}

$returnContent = $return | ConvertFrom-Json
$contractId = $returnContent.value[0].id
Write-Host "Contract ID list: $contractId"

$return = curl --fail-with-body -sS -X GET http://localhost:61001/collaborations/collab1/contracts/${contractId}?api-version=2026-03-01-preview `
    -H "content-type: application/json" `
    -H "Authorization: Bearer $userToken"

$returnContent = $return | ConvertFrom-Json
Write-Host "Contract details:"
$returnContent | Get-Member -MemberType Properties | ForEach-Object {
    $propertyName = $_.Name
    $propertyValue = $returnContent.$propertyName
    Write-Host "  $propertyName : $propertyValue"
}

Write-Host "Voting on Document:"
$jsonPayload = @{
    "proposalId"        = "proposal-12345"
} | ConvertTo-Json -Depth 10
$return = curl --fail-with-body -sS -X POST http://localhost:61001/collaborations/query/consumer-output-cba976a8/voteaccept?api-version=2026-03-01-preview `
    -H "content-type: application/json" `
    -H "Authorization: Bearer $userToken" `
    -d $jsonPayload
$returnContent = $return | ConvertFrom-Json
$returnContent
Write-Host "CGS call successful"

#>
