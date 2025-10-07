[CmdletBinding()]
param
(
  [string]
  $outDir = ""
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
az cleanroom cluster get-kubeconfig `
  --name $clCluster.name `
  --infra-type $clCluster.infraType `
  --provider-config $sandbox_common/providerConfig.json `
  -f $kubeConfig

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
    Write-Host "Waiting for analytics agent endpoint to be up at ${frontendSvcAddress}/ready"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
      # Re-run the command once to log its output.
      curl -k -s ${frontendSvcAddress}/ready
      throw "Hit timeout waiting for analytics agent endpoint to be up."
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
