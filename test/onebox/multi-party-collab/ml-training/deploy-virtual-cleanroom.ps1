[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated",

    [string]
    $datastoreOutdir = "$PSScriptRoot/generated/datastores",

    [switch]
    $nowait,

    [switch]
    $skiplogs
)

$root = git rev-parse --show-toplevel

kubectl delete pod virtual-cleanroom --force
kubectl apply -f $outDir/deployments/virtual-cleanroom-pod.yaml

kubectl wait --for=condition=ready pod -l app=virtual-cleanroom --timeout=180s
# https://dustinspecker.com/posts/resolving-kubernetes-services-from-host-when-using-kind/
$podIP = docker exec cleanroom-control-plane kubectl get pod virtual-cleanroom -o jsonpath="{.status.podIP}"
Write-Host "Pod IP address: $podIP"

# wait for code-launcher endpoint to be up.
$timeout = New-TimeSpan -Minutes 15
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ((docker exec cleanroom-control-plane curl -o /dev/null -w "%{http_code}" -s -k https://${podIP}:8200/gov/doesnotexist/status) -ne "404") {
    Write-Host "Waiting for code-launcher endpoint to be up at https://${podIP}:8200"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for code-launcher endpoint to be up."
    }
}

# The application is configured for auto-start. Hence, no need to issue the start API.
# docker exec cleanroom-control-plane curl -X POST -s -k https://${podIP}:8200/gov/depa-training/start

if (!$nowait) {
    pwsh $root/test/onebox/multi-party-collab/wait-for-virtual-cleanroom.ps1 `
        -appName depa-training `
        -cleanroomIp $podIP
}

docker exec cleanroom-control-plane curl -X POST -s -k https://${podIP}:8200/gov/exportLogs
docker exec cleanroom-control-plane curl -X POST -s -k https://${podIP}:8200/gov/exportTelemetry

if (!$skiplogs) {
    mkdir -p $outDir/results
    az cleanroom datastore download `
        --config $datastoreOutdir/ml-training-consumer-datastore-config `
        --name output `
        --dst $outDir/results

    az cleanroom telemetry download `
        --cleanroom-config $outDir/configurations/tdp-config `
        --datastore-config $datastoreOutdir/ml-training-publisher-datastore-config `
        --target-folder $outDir/results

    az cleanroom logs download `
        --cleanroom-config $outDir/configurations/tdp-config `
        --datastore-config $datastoreOutdir/ml-training-publisher-datastore-config `
        --target-folder $outDir/results

    Write-Host "Application logs:"
    cat $outDir/results/application-telemetry*/**/depa-training.log
}