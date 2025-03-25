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

    # wait for tritonserver endpoint to be up. The image is big so it takes time for podman to pull it within the container.
    $timeout = New-TimeSpan -Minutes 20
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ((docker exec cleanroom-control-plane curl -o /dev/null -w "%{http_code}" -s -k https://${podIP}:8000/v2/health/ready) -ne "200") {
        Write-Host "Waiting for tritonserver endpoint to be up at https://${podIP}:8000/v2/health/ready ($($stopwatch.elapsed.ToString()))"
        Start-Sleep -Seconds 20
        if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for tritonserver endpoint to be up."
        }
    }
    $stopwatch.Stop()
    Write-Host "Waited for ($($stopwatch.elapsed.ToString()))"

    # Latest was "nvcr.io/nvidia/tritonserver:24.06-py3" but its based off ubuntu 22.04 that does not work with CACI yet.
    $tritonServerImage = "nvcr.io/nvidia/tritonserver:22.05-py3"
    $tritonServerSdkImage = $tritonServerImage + "-sdk"

    Get-Job -Command "*kubectl port-forward virtual-cleanroom*" | Stop-Job
    Get-Job -Command "*kubectl port-forward virtual-cleanroom*" | Remove-Job
    kubectl port-forward virtual-cleanroom 8000:8000 &
    Start-Sleep -Seconds 8
    docker run -it --rm --net=host $tritonServerSdkImage `
        /workspace/install/bin/image_client -m densenet_onnx -c 3 -s INCEPTION `
        /workspace/images/mug.jpg -u host.docker.internal:8000
}

if (!$skiplogs) {
    # TODO (gsinha): Add logic to download logs
    # mkdir -p $outDir/results
}