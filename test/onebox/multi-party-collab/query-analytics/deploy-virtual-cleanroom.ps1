[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated",

    [switch]
    $nowait,

    [switch]
    $skiplogs
)

$root = git rev-parse --show-toplevel

kubectl delete pod virtual-cleanroom --force
kubectl apply -f $outDir/deployments/virtual-cleanroom-pod.yaml
if (!$nowait) {
    # Wait for the pod to be ready before fetching its IP.
    kubectl wait --for=condition=ready pod -l app=virtual-cleanroom --timeout=180s
    # https://dustinspecker.com/posts/resolving-kubernetes-services-from-host-when-using-kind/
    $podIP = kubectl get pod virtual-cleanroom -o jsonpath="{.status.podIP}"
    Write-Host "Pod IP address: $podIP"

    # wait for pyspark endpoint to be up.
    $timeout = New-TimeSpan -Minutes 10
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ((docker exec cleanroom-control-plane curl -o /dev/null -w "%{http_code}" -s -k https://${podIP}:8310/app/run_query/12) -ne "200") {
        Write-Host "Waiting for pyspark endpoint to be up at https://${podIP}:8310"
        Start-Sleep -Seconds 3
        if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for pyspark endpoint to be up."
        }
    }

    Write-Host "Successfully connected to the pyspark endpoint at https://${podIP}:8310"
}

if (!$skiplogs) {
    mkdir -p $outDir/results
    az cleanroom datasink download `
        --cleanroom-config $outDir/configurations/consumer-config `
        --name consumer-output `
        --target-folder $outDir/results

    az cleanroom telemetry download `
        --cleanroom-config $outDir/configurations/publisher-config `
        --target-folder $outDir/results

    az cleanroom logs download `
        --cleanroom-config $outDir/configurations/publisher-config `
        --target-folder $outDir/results

    Write-Host "Application logs:"
    cat $outDir/results/application-telemetry/demo-app.log
}