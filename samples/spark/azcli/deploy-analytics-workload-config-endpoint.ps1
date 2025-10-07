[CmdletBinding()]
param
(
  [Parameter(Mandatory)]
  [string]$repo,

  [Parameter(Mandatory)]
  [string]$tag,

  [string]
  $outDir = ""
)

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

@"
{
  "data": {
    "values": {
      "sparkFrontendEndpoint": "https://cleanroom-spark-frontend.cleanroom-spark-frontend.svc",
      "sparkFrontendSnpHostData": "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"
    }
  }
}
"@ > $sandbox_common/analytics-agent.deployment-config.json

# Start a simple web server that hosts the deployment configuration file. This URL is
# supplied as input to enable workload so that it fetches the deployment config from
# the web server.
Write-Output "Starting simple web server to host the deployment configuration endpoint."
$containerName = "simple-nginx"
$containerPort = 8998
docker rm -f $containerName 1>$null 2>$null
docker run `
  -d `
  --name $containerName `
  -p ${containerPort}:80 `
  -v $sandbox_common/:/usr/share/nginx/html:ro `
  mcr.microsoft.com/mirror/docker/library/nginx:1.25
if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
  $nginxEndpoint = "http://host.docker.internal:$containerPort"
}
else {
  # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
  $nginxEndpoint = "http://172.17.0.1:$containerPort"
}
@"
{
  "url": "${nginxEndpoint}/analytics-agent.deployment-config.json",
  "caCert": ""
}
"@ > $sandbox_common/analytics-workload-config-endpoint.json
