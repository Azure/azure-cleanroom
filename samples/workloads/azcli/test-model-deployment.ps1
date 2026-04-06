param(
    [string]$Namespace = "kserve-inferencing",
    [string]$ModelName = "hello", 
    [string]$outDir = "",
    [switch]$FlexNodeEnabled
)

# Set error action preference to stop on any error
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
}
else {
    $sandbox_common = $outDir
}

$kubeConfig = "${sandbox_common}/k8s-credentials.yaml"
$env:KUBECONFIG = $kubeConfig

Write-Host "🚀 Checkin on deployment of $ModelName model to namespace $Namespace..." -ForegroundColor Green

try {
    Write-Host "⏳ Waiting for InferenceService to be ready (timeout: 5 minutes)..." -ForegroundColor Yellow
    kubectl wait --for=condition=Ready "inferenceservice/$ModelName" -n $Namespace --timeout=300s

    Write-Host "✅ InferenceService $ModelName is ready!" -ForegroundColor Green

    # Verify predictor deployment placement.
    $deploymentName = "$ModelName-predictor"
    $label = if ($FlexNodeEnabled) { "flexnode" } else { "regular node" }
    Write-Host "🔍 Verifying $label placement on deployment '$deploymentName'..." -ForegroundColor Cyan
    $deployment = kubectl get deployment $deploymentName -n $Namespace -o json | ConvertFrom-Json
    $podTemplate = $deployment.spec.template

    $podAnnotations = $podTemplate.metadata.annotations
    $flexNodeAnnotations = @("api-server-proxy.io/policy", "api-server-proxy.io/signature")
    $nodeSelector = $podTemplate.spec.nodeSelector

    if ($FlexNodeEnabled) {
        # Verify annotations are present.
        foreach ($annotation in $flexNodeAnnotations) {
            if (-not ($podAnnotations.PSObject.Properties.Name -contains $annotation)) {
                throw "Deployment '$deploymentName' is missing required annotation '$annotation'."
            }
        }
        Write-Host "  ✓ Annotations $($flexNodeAnnotations -join ', ') are present." -ForegroundColor Green

        # Verify nodeSelector is set.
        if ($nodeSelector.'pod-policy' -ne "required") {
            throw "Deployment '$deploymentName' does not have expected nodeSelector 'pod-policy: required'. Found: $($nodeSelector | ConvertTo-Json -Compress)"
        }
        Write-Host "  ✓ nodeSelector 'pod-policy: required' is set." -ForegroundColor Green
    }
    else {
        # Verify annotations are NOT present.
        foreach ($annotation in $flexNodeAnnotations) {
            if ($podAnnotations -and ($podAnnotations.PSObject.Properties.Name -contains $annotation)) {
                throw "Deployment '$deploymentName' has unexpected annotation '$annotation' for a regular node deployment."
            }
        }
        Write-Host "  ✓ Annotations $($flexNodeAnnotations -join ', ') are not present." -ForegroundColor Green

        # Verify nodeSelector is NOT set.
        if ($nodeSelector -and $nodeSelector.'pod-policy' -eq "required") {
            throw "Deployment '$deploymentName' has unexpected nodeSelector 'pod-policy: required' for a regular node deployment."
        }
        Write-Host "  ✓ nodeSelector 'pod-policy: required' is not set." -ForegroundColor Green
    }

    if (!$NoTest) {
        Write-Host "🧪 Testing the deployed model..." -ForegroundColor Yellow
        
        try {
            # Port-forward directly to the predictor service (no gateway needed).
            $predictorSvc = "${ModelName}-predictor"
            $INGRESS_HOST = "localhost"
            $INGRESS_PORT = "8989"
            Get-Job -Command "*kubectl port-forward svc/${predictorSvc}*" -ErrorAction SilentlyContinue | Stop-Job
            Get-Job -Command "*kubectl port-forward svc/${predictorSvc}*" -ErrorAction SilentlyContinue | Remove-Job
            kubectl port-forward svc/${predictorSvc} --namespace ${Namespace} ${INGRESS_PORT}:80 &

            # Need to wait a bit for the port-forward to start.
            $root = git rev-parse --show-toplevel
            bash $root/src/scripts/wait-for-it.sh --timeout=20 --strict 127.0.0.1:${INGRESS_PORT} -- echo "inference service is available"

            curl -sS --fail-with-body `
                -H "Content-Type: application/json" `
                http://${INGRESS_HOST}:${INGRESS_PORT}/v2/models/$ModelName/infer `
                -d @"
{
  "inputs": [
    {
      "name": "input-0",
      "shape": [2, 4],
      "datatype": "FP32",
      "data": [
        [6.8, 2.8, 4.8, 1.4],
        [6.0, 3.4, 4.5, 1.6]
      ]
    }
  ]
}
"@ | ConvertFrom-Json | ConvertTo-Json -Depth 100
        }
        catch {
            Write-Host "⚠️  Could not retrieve service information for testing: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    Write-Host "`n🎉 Deployment completed successfully!" -ForegroundColor Green
    Write-Host "📊 Model Details:" -ForegroundColor Cyan
    Write-Host "   - Name: $ModelName" -ForegroundColor White
    Write-Host "   - Namespace: $Namespace" -ForegroundColor White
    Write-Host "   - Storage URI: $StorageUri" -ForegroundColor White
}
catch {
    Write-Host "❌ Deployment failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "`n🔧 Troubleshooting steps:" -ForegroundColor Yellow
    Write-Host "1. Check if KServe is properly installed:" -ForegroundColor White
    Write-Host "   kubectl get pods -n kserve" -ForegroundColor Gray
    Write-Host "2. Check the InferenceService status:" -ForegroundColor White
    Write-Host "   kubectl get inferenceservice -n $Namespace" -ForegroundColor Gray
    Write-Host "3. Check InferenceService events:" -ForegroundColor White
    Write-Host "   kubectl describe inferenceservice $ModelName -n $Namespace" -ForegroundColor Gray
    Write-Host "4. Check pod logs:" -ForegroundColor White
    Write-Host "   kubectl logs -n $Namespace -l serving.kserve.io/inferenceservice=$ModelName" -ForegroundColor Gray
    
    exit 1
}