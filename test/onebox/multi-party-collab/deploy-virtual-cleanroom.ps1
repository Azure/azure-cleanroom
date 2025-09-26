[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated",

    [Parameter(Mandatory = $true)]
    [string]
    $repo,

    [Parameter(Mandatory = $true)]
    [string]
    $tag
)

$root = git rev-parse --show-toplevel

kubectl delete pod virtual-cleanroom --force
kubectl apply -f $outDir/deployments/virtual-cleanroom-pod.yaml

Write-Host "Waiting for pod to become ready"
kubectl wait --for=condition=ready pod -l app=virtual-cleanroom --timeout=180s
# https://dustinspecker.com/posts/resolving-kubernetes-services-from-host-when-using-kind/
$podIP = kubectl get pod virtual-cleanroom -o jsonpath="{.status.podIP}"
Write-Host "Pod IP address: $podIP"
if ($podIP -eq "") {
    throw "Failed to get Pod IP address for virtual-cleanroom."
}

# wait for code-launcher endpoint to be up.
$timeout = New-TimeSpan -Minutes 5
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$statusCode = docker exec cleanroom-control-plane curl -o /dev/null -w "%{http_code}" -s -k https://${podIP}:8200/gov/doesnotexist/status
while ($statusCode -ne "404") {
    Write-Host "Waiting for code-launcher endpoint to be up at https://${podIP}:8200. Status code: $statusCode"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for code-launcher endpoint to be up."
    }
    $statusCode = docker exec cleanroom-control-plane curl -o /dev/null -w "%{http_code}" -s -k https://${podIP}:8200/gov/doesnotexist/status
}

Write-Host "Code launcher is up."
Start-Sleep -Seconds 10

# Do a get status and record response.
$expectedResponse = '{"detail":"Application not found"}'
$response = docker exec cleanroom-control-plane curl -s -k https://${podIP}:8200/gov/doesnotexist/status
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response. ExpectedResponse: $expectedResponse."
    exit 1
}

$base64CaCert = $(cat $outDir/cleanroomca.crt | base64 -w 0)

# Run the same scenarios via the ccr-client-proxy that then avoids insecure "curl -k" method used above.
@"
apiVersion: v1
kind: Pod
metadata:
  name: ccr-client-proxy
  labels:
    app: ccr-client-proxy
spec:
  containers:
  - name: ccr-client-proxy
    image: $repo/ccr-client-proxy:$tag
    command:
    - /bin/bash
    - -c
    - ./bootstrap.sh
    env:
    - name: CA_CERT
      value: $base64CaCert
  restartPolicy: Never
  hostAliases:
  - ip: "$podIP"
    hostnames:
    - "ccr.cleanroom.local"
"@ > $outDir/deployments/client-proxy-pod.yaml

& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    kubectl delete pod ccr-client-proxy --force
}

kubectl apply -f $outDir/deployments/client-proxy-pod.yaml
Write-Host "Waiting for pod to become ready"
kubectl wait --for=condition=ready pod -l app=ccr-client-proxy --timeout=180s

Get-Job -Command "*kubectl port-forward ccr-client-proxy*" | Stop-Job
Get-Job -Command "*kubectl port-forward ccr-client-proxy*" | Remove-Job
kubectl port-forward ccr-client-proxy 10081:10080 &

# Need to wait a bit for the port-forward to start.
bash $root/src/scripts/wait-for-it.sh --timeout=20 --strict 127.0.0.1:10081 -- echo "ccr-client-proxy is available"

# below should fail
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$statusCode = curl -o /dev/null -w "%{http_code}" -s http://ccr.cleanroom.local:8200/gov/blah-code-check --proxy http://127.0.0.1:10081
while ($statusCode -ne "403") {
    Write-Host "Waiting for ccr-client-proxy endpoint to be up. Status code: $statusCode"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for ccr-client-proxy endpoint to be up."
    }
    $statusCode = curl -o /dev/null -w "%{http_code}" -s http://ccr.cleanroom.local:8200/gov/blah --proxy http://127.0.0.1:10081
}

# below should fail
$expectedResponse = '{"code":"RequestNotAllowed","message":"Failed ccr policy check: Requested API is not allowed"}'
$response = curl -s http://ccr.cleanroom.local:8200/gov/blah-response-check --proxy http://127.0.0.1:10081
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response. ExpectedResponse: $expectedResponse."
    exit 1
}