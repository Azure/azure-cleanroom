#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Deploy scikit-learn Iris model to KServe platform

.DESCRIPTION
    This script deploys a sample scikit-learn Iris classification model to the KServe platform.
    It creates the necessary namespace, deploys the InferenceService, waits for it to be ready,
    and provides sample test data for validation.

.PARAMETER Namespace
    The Kubernetes namespace to deploy the model to. Default: kserve-test

.PARAMETER ModelName
    The name of the model to deploy. Default: sklearn-v2-iris

.PARAMETER StorageUri
    The storage URI where the model is located. Default: gs://kfserving-examples/models/sklearn/1.0/model

.PARAMETER NoTest
    Whether to skip testing the model after deployment. Default: $false

.EXAMPLE
    ./deploy-iris.ps1
    
.EXAMPLE
    ./deploy-iris.ps1 -Namespace "ml-models" -ModelName "iris-classifier" -NoTest
#>

param(
    [string]$Namespace = "kserve-test",
    [string]$ModelName = "sklearn-v2-iris", 
    [string]$StorageUri = "gs://kfserving-examples/models/sklearn/1.0/model",
    [switch]$NoTest = $false
)

# Set error action preference to stop on any error
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

Write-Host "🚀 Starting deployment of $ModelName model to namespace $Namespace..." -ForegroundColor Green

try {
    Write-Host "📦 Creating namespace $Namespace..." -ForegroundColor Yellow
    $namespaceYaml = kubectl create namespace $Namespace --dry-run=client -o yaml
    $namespaceYaml | kubectl apply -f -
    
    Write-Host "🤖 Creating InferenceService for $ModelName..." -ForegroundColor Yellow
    
    # Create the InferenceService YAML content
    $inferenceServiceYaml = @"
apiVersion: "serving.kserve.io/v1beta1"
kind: "InferenceService"
metadata:
  name: $ModelName
  namespace: $Namespace
spec:
  predictor:
    model:
      modelFormat:
        name: sklearn
      protocolVersion: v2
      runtime: kserve-sklearnserver
      storageUri: $StorageUri
"@

    # Apply the InferenceService
    $inferenceServiceYaml | kubectl apply -f -

    Write-Host "⏳ Waiting for InferenceService to be ready (timeout: 5 minutes)..." -ForegroundColor Yellow
    kubectl wait --for=condition=Ready "inferenceservice/$ModelName" -n $Namespace --timeout=300s

    Write-Host "✅ InferenceService $ModelName is ready!" -ForegroundColor Green
    if (!$NoTest) {
        Write-Host "🧪 Testing the deployed model..." -ForegroundColor Yellow
        
        try {
            $INGRESS_GATEWAY_SERVICE = $(kubectl get svc -l serving.kserve.io/gateway=kserve-ingress-gateway -A --output jsonpath='{.items[0].metadata.name}')
            $INGRESS_GATEWAY_NAMESPACE = $(kubectl get svc -l serving.kserve.io/gateway=kserve-ingress-gateway -A --output jsonpath='{.items[0].metadata.namespace}')
            
            $INGRESS_HOST = "localhost"
            $INGRESS_PORT = "8989"
            Get-Job -Command "*kubectl port-forward svc/${INGRESS_GATEWAY_SERVICE}*" | Stop-Job
            Get-Job -Command "*kubectl port-forward svc/${INGRESS_GATEWAY_SERVICE}*" | Remove-Job
            kubectl port-forward svc/${INGRESS_GATEWAY_SERVICE} --namespace ${INGRESS_GATEWAY_NAMESPACE} ${INGRESS_PORT}:80 &

            # Need to wait a bit for the port-forward to start.
            $root = git rev-parse --show-toplevel
            bash $root/src/scripts/wait-for-it.sh --timeout=20 --strict 127.0.0.1:${INGRESS_PORT} -- echo "inference service is available"

            $SERVICE_HOSTNAME = $(kubectl get inferenceservice $ModelName -n $Namespace -o jsonpath='{.status.url}' | cut -d "/" -f 3)

            curl -sS --fail-with-body `
                -H "Host: ${SERVICE_HOSTNAME}" `
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
