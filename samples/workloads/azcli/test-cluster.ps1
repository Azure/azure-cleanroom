[CmdletBinding()]
param
(
  [string]
  $outDir = "",

  [switch]
  $testAnalytics,

  [switch]
  $testKServeInferencing,

  [switch]
  $testFlexNode
)

function Get-TimeStamp {
  return "[{0:MM/dd/yy} {0:HH:mm:ss}]" -f (Get-Date)
}

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

if ($outDir -eq "") {
  $sandbox_common = "$PSScriptRoot/sandbox_common"
}
else {
  $sandbox_common = $outDir
}

$clCluster = Get-Content $sandbox_common/cl-cluster.json | ConvertFrom-Json
$kubeConfig = "${sandbox_common}/k8s-credentials.yaml"

function Test-DiagnosticK8sCredentials {
  param (
    [string]$sandbox_common,
    [object]$clCluster,
    [switch]$observabilityEnabled
  )

  Write-Host "Getting diagnostic k8s credentials..."
  $diagnosticKubeConfig = "${sandbox_common}/k8s-diagnostic-credentials.yaml"
  az cleanroom cluster get-kubeconfig `
    --name $clCluster.name `
    --infra-type $clCluster.infraType `
    --access-role diagnostic `
    --provider-config $sandbox_common/providerConfig.json `
    -f $diagnosticKubeConfig

  Write-Host "Verifying diagnostic k8s credentials..."
  Write-Host "Getting pods in telemetry namespace using diagnostic k8s credentials..."
  kubectl get pods -n telemetry --kubeconfig $diagnosticKubeConfig
  if ($LASTEXITCODE -ne 0) {
    throw "Failed to get pods in telemetry namespace using diagnostic k8s credentials."
  }

  Write-Host "Getting pods in default namespace using diagnostic k8s credentials..."
  & {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    $response = kubectl get pods -n default --kubeconfig $diagnosticKubeConfig 2>&1
    if ($LASTEXITCODE -ne 0) {
      throw "Get pods command should have worked with diagnostic credentials."
    }
  }

  Write-Host "Getting config maps in default namespace using diagnostic k8s credentials..."
  & {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false

    $expectedResponse = "Error from server (Forbidden)"
    $response = kubectl get configmaps -n default --kubeconfig $diagnosticKubeConfig 2>&1
    if ($LASTEXITCODE -eq 0) {
      throw "Get config maps command should have failed with forbidden error."
    }
    else {
      if ($response.Exception.Message.StartsWith($expectedResponse)) {
        Write-Host "Diagnostic k8s credentials verified to not have access to other resources in default namespace."
      }
      else {
        throw "Expected response to contain $expectedResponse, got $response instead."
      }
    }
  }
}

function Test-ReadonlyK8sCredentials {
  param (
    [string]$sandbox_common,
    [object]$clCluster
  )

  Write-Host "Getting readonly k8s credentials..."
  $readonlyKubeConfig = "${sandbox_common}/k8s-readonly-credentials.yaml"
  az cleanroom cluster get-kubeconfig `
    --name $clCluster.name `
    --infra-type $clCluster.infraType `
    --access-role readonly `
    --provider-config $sandbox_common/providerConfig.json `
    -f $readonlyKubeConfig

  Write-Host "Verifying readonly k8s credentials..."
  Write-Host "Getting pods in spark-operator namespace using readonly k8s credentials..."
  kubectl get pods -n spark-operator --kubeconfig $sandbox_common/k8s-readonly-credentials.yaml
  if ($LASTEXITCODE -ne 0) {
    throw "Failed to get pods in spark-operator namespace using readonly k8s credentials."
  }

  Write-Host "Trying to create a pod using readonly k8s credentials...."
  & {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false

    $expectedResponse = "Error from server (Forbidden)"
    $response = kubectl run my-pod --image=nginx --restart=Never --kubeconfig $sandbox_common/k8s-readonly-credentials.yaml 2>&1

    if ($LASTEXITCODE -eq 0) {
      throw "Create pod command should have failed with forbidden error."
    }
    else {
      if ($response.Exception.Message.StartsWith($expectedResponse)) {
        Write-Host "Readonly k8s credentials verified to not have write access."
      }
      else {
        throw "Expected response to contain $expectedResponse, got $response instead."
      }
    }
  }
}

$clusterInfo = Get-Content $sandbox_common/cl-cluster.json | ConvertFrom-Json
Test-ReadonlyK8sCredentials -sandbox_common $sandbox_common -clCluster $clCluster
$observabilityEnabled = $clusterInfo.observabilityWorkloadProfile -ne $null -and $clusterInfo.observabilityWorkloadProfile.enabled -eq $true
if ($observabilityEnabled) {
  Write-Host "Testing diagnostic k8s credentials...."
  Test-DiagnosticK8sCredentials -sandbox_common $sandbox_common -clCluster $clCluster -observabilityEnabled:$observabilityEnabled
  Write-Host -ForegroundColor Green "Diagnostic k8s credentials test completed successfully!"
}
else {
  Write-Host "Observability workload is not enabled, skipping diagnostic credentials tests."
}

$analyticsEnabled = $clusterInfo.analyticsWorkloadProfile -ne $null -and $clusterInfo.analyticsWorkloadProfile.enabled -eq $true
if ($analyticsEnabled) {
  Write-Host "Testing analytics workload functionality..."

  # We will use kubectl proxy to access the frontend service via localhost.

  Get-Job -Command "*kubectl proxy --port 8282*" | Stop-Job
  Get-Job -Command "*kubectl proxy --port 8282*" | Remove-Job
  kubectl proxy --port 8282 --kubeconfig $kubeConfig &

  $frontendSvcAddress = "http://localhost:8282/api/v1/namespaces/cleanroom-spark-frontend/services/https:cleanroom-spark-frontend:443/proxy"

  $timeout = New-TimeSpan -Minutes 10
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
  & {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -o /dev/null -w "%{http_code}" -k -s ${frontendSvcAddress}/ready) -ne "200") {
      Write-Host "Waiting for analytics frontend endpoint to be up at ${frontendSvcAddress}/ready"
      Start-Sleep -Seconds 3
      if ($stopwatch.elapsed -gt $timeout) {
        # Re-run the command once to log its output.
        curl -k -s ${frontendSvcAddress}/ready
        throw "Hit timeout waiting for analytics frontend endpoint to be up."
      }
    }
  }
  Write-Host "Submitting pi job to $($frontendSvcAddress)/analytics/submitPiJob"
  $submissionJson = $(curl --silent --fail-with-body `
      -X POST -k `
      $frontendSvcAddress/analytics/submitPiJob)

  Write-Host "Submission response: $submissionJson"
  $submissionResult = $submissionJson | ConvertFrom-Json
  $jobId = $submissionResult.id

  # Save the job details to a file so as to extract logs later.
  @"
{
  "jobId": "$jobId"
}
"@ > $sandbox_common/jobConfig.json

  $applicationTimeout = New-TimeSpan -Minutes 30
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

  Write-Host "Waiting for application $jobId execution to complete..."
  do {
    Write-Host "$(Get-TimeStamp) Checking status of job: $jobId"

    $jobStatusResponse = $(curl --silent `
        -X GET -k `
        $frontendSvcAddress/analytics/status/$jobId)

    $jobStatus = $jobStatusResponse | ConvertFrom-Json

    if ($jobStatus.status.applicationState.state -eq "COMPLETED") {
      Write-Host -ForegroundColor Green "$(Get-TimeStamp) Application has completed execution."
      break
    }

    if ($jobStatus.status.applicationState.state -eq "FAILED") {
      Write-Host -ForegroundColor Red "$(Get-TimeStamp) Application has failed execution."
      exit 1
    }

    Write-Host "Application $jobId state is: $($jobStatus.status.applicationState.state)"

    if ($stopwatch.elapsed -gt $applicationTimeout) {
      throw "Hit timeout waiting for application $jobId to complete execution."
    }

    Write-Host "Waiting for 10 seconds before checking status again..."
    Start-Sleep -Seconds 10
  } while ($true)

  Write-Host -ForegroundColor Green "Analytics workload test completed successfully!"
}
else {
  if ($testAnalytics) {
    throw "Analytics workload is not enabled, cannot run analytics tests."
  }
  Write-Host "Analytics workload is not enabled, skipping analytics tests."
}

$inferencingEnabled = $clusterInfo.inferencingWorkloadProfile -ne $null -and $clusterInfo.inferencingWorkloadProfile.KServeProfile -ne $null -and $clusterInfo.inferencingWorkloadProfile.KServeProfile.enabled -eq $true
if ($inferencingEnabled) {
  Write-Host "Testing inferencing workload functionality..."

  # We will use kubectl proxy to access the frontend service via localhost.

  Get-Job -Command "*kubectl proxy --port 8686*" | Stop-Job
  Get-Job -Command "*kubectl proxy --port 8686*" | Remove-Job
  kubectl proxy --port 8686 --kubeconfig $kubeConfig &

  $frontendSvcAddress = "http://localhost:8686/api/v1/namespaces/kserve-inferencing-frontend/services/https:kserve-inferencing-frontend:443/proxy"

  $timeout = New-TimeSpan -Minutes 10
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
  $modelName = "hello"
  & {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -o /dev/null -w "%{http_code}" -k -s ${frontendSvcAddress}/ready) -ne "200") {
      Write-Host "Waiting for inferencing frontend endpoint to be up at ${frontendSvcAddress}/ready"
      Start-Sleep -Seconds 3
      if ($stopwatch.elapsed -gt $timeout) {
        # Re-run the command once to log its output.
        curl -k -s ${frontendSvcAddress}/ready
        throw "Hit timeout waiting for inferencing frontend endpoint to be up."
      }
    }
  }
  $deployTestUrl = "$frontendSvcAddress/inferencing/test/deployModel"
  $flexNodeEnabled = $clusterInfo.flexNodeProfile -ne $null -and $clusterInfo.flexNodeProfile.enabled -eq $true
  if ($flexNodeEnabled) {
    Write-Host "Flex node is enabled. Getting security policy and signing it..."

    # Get the security policy from the frontend.
    $policyUrl = "$frontendSvcAddress/inferencing/test/generateSecurityPolicy?node_type=flexnode"
    Write-Host "Getting security policy from: $policyUrl"
    $policyResponse = $(curl --silent --fail-with-body -X POST -k $policyUrl)
    Write-Host "Policy response: $policyResponse"
    $policyResult = $policyResponse | ConvertFrom-Json
    $policyBase64 = $policyResult.predictor.jsonBase64

    # Sign the policy using policy-signing-tool.sh.
    $signingConfigFile = "$sandbox_common/signing-config.json"
    if (-not (Test-Path $signingConfigFile)) {
      throw "Signing config not found at $signingConfigFile. " +
      "Run generate-signing-keys.ps1 first."
    }
    $signingConfig = Get-Content $signingConfigFile | ConvertFrom-Json
    $signingKeyDir = $signingConfig.signingKeyDir
    $repoRoot = git rev-parse --show-toplevel
    $signingTool = "$repoRoot/src/k8s-node/api-server-proxy/scripts/policy-signing-tool.sh"

    Write-Host "Signing policy using policy-signing-tool.sh..."
    $signature = bash $signingTool --key-dir $signingKeyDir sign $policyBase64

    Write-Host "Policy signed successfully. Signature: $signature"
    Write-Host "Using node type: flexnode for model deployment."
  }

  # Clean up any existing InferenceService instance of the model.
  Write-Host "Checking for existing InferenceService '$modelName'..." -ForegroundColor Yellow
  $existing = kubectl get inferenceservice $modelName -n kserve-inferencing `
    --kubeconfig $kubeConfig --ignore-not-found -o name
  if ($existing) {
    Write-Host "Deleting existing InferenceService '$modelName'..." -ForegroundColor Yellow
    kubectl delete inferenceservice $modelName -n kserve-inferencing --kubeconfig $kubeConfig
    Write-Host "Successfully deleted InferenceService '$modelName'." -ForegroundColor Green
  }
  else {
    Write-Host "No existing InferenceService '$modelName' found. Nothing to clean up." -ForegroundColor Green
  }

  Write-Host "Deploying test model as $deployTestUrl"
  $deployBody = @{ modelName = $modelName }
  if ($flexNodeEnabled) {
    $deployBody.nodeType = "flexnode"
    if ($clCluster.infraType -eq "aks") {
      $deployBody.hostNetwork = $true
    }
    if ($signature) {
      $deployBody.signature = $signature
    }
  }
  $deployBodyJson = $deployBody | ConvertTo-Json
  $submissionJson = $(curl --silent --fail-with-body `
      -X POST -k `
      -H "Content-Type: application/json" `
      -d $deployBodyJson `
      $deployTestUrl)

  Write-Host "Submission response: $submissionJson"
  $submissionResult = $submissionJson | ConvertFrom-Json

  $applicationTimeout = New-TimeSpan -Minutes 30
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

  Write-Host "Waiting for model $modelName deployment to complete..."
  do {
    Write-Host "$(Get-TimeStamp) Checking status of model: $modelName"

    $jobStatusResponse = $(curl --silent `
        -X GET -k `
        $frontendSvcAddress/inferencing/status/$modelName)

    $jobStatus = $jobStatusResponse | ConvertFrom-Json

    # Check PredictorReady condition (works with and without gateway).
    $predictorReady = $jobStatus.status.conditions | Where-Object { $_.type -eq "PredictorReady" -and $_.status -eq "True" }
    $url = $jobStatus.status.url
    Write-Host "Model $modelName PredictorReady: $($predictorReady -ne $null), url: $url"

    if ($predictorReady -ne $null) {
      Write-Host -ForegroundColor Green "$(Get-TimeStamp) Model has completed deployment."
      break
    }

    if ($stopwatch.elapsed -gt $applicationTimeout) {
      throw "Hit timeout waiting for model $modelName to complete deployment."
    }

    Write-Host "Waiting for 10 seconds before checking status again..."
    Start-Sleep -Seconds 10
  } while ($true)

  $testModelDeploymentArgs = @{
    Namespace = "kserve-inferencing"
    ModelName = $modelName
  }
  if ($flexNodeEnabled) {
    $testModelDeploymentArgs.FlexNodeEnabled = $true
  }
  pwsh $PSScriptRoot/test-model-deployment.ps1 @testModelDeploymentArgs

  Write-Host -ForegroundColor Green "KServe inferencing workload test completed successfully!"
}
else {
  if ($testKServeInferencing) {
    throw "KServe inferencing workload is not enabled, cannot run KServe inferencing tests."
  }
  Write-Host "KServe inferencing workload is not enabled, skipping KServe inferencing tests."
}

$flexNodeEnabled = $clusterInfo.flexNodeProfile -ne $null -and $clusterInfo.flexNodeProfile.enabled -eq $true
if ($flexNodeEnabled) {
  Write-Host "Testing flex node pod policies..."

  pwsh $PSScriptRoot/test-pod-policies.ps1

  Write-Host -ForegroundColor Green "Flex node pod policies test completed successfully!"
}
else {
  if ($testFlexNode) {
    throw "Flex node is not enabled, cannot run flex node tests."
  }
  Write-Host "Flex node is not enabled, skipping flex node tests."
}