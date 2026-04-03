[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]$kubeConfig,

    [int]$TailLines = 20000,
    [int]$MaxRetries = 3,
    [int]$RetryDelaySeconds = 2
)


#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Helper function to retry kubectl commands
function Invoke-KubectlWithRetry {
    param(
        [string]$Command,
        [int]$MaxRetries = $script:MaxRetries,
        [int]$InitialDelaySeconds = $script:RetryDelaySeconds
    )
    
    $attempt = 0
    $delay = $InitialDelaySeconds
    
    while ($attempt -lt $MaxRetries) {
        try {
            Write-Verbose "Executing: kubectl $Command"
            $result = Invoke-Expression "kubectl $Command 2>&1"
            return $result
        }
        catch {
            $attempt++
            if ($attempt -ge $MaxRetries) {
                Write-Error "Failed after $MaxRetries attempts: $_"
                throw
            }
            Write-Warning "Attempt $attempt failed: $_. Retrying in $delay seconds..."
            Start-Sleep -Seconds $delay
            $delay *= 2  # Exponential backoff
        }
    }
}

Write-Host "KServe inferencing agent logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=kserve-inferencing-agent -n kserve-inferencing-agent --timestamps=true --tail=$TailLines --all-containers=true --prefix=true --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

Write-Host "KServe inferencing frontend logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=kserve-inferencing-frontend -n kserve-inferencing-frontend --timestamps=true --all-containers=true --prefix=true --tail=$TailLines --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"
