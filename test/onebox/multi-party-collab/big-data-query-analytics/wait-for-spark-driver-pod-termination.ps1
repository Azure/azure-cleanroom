param (
    [string]$PodName,
    [string]$Namespace = "default",
    [string]$KubeConfig,
    [int]$TimeoutSeconds = 300,
    [int]$PollIntervalSeconds = 5
)

function Wait-ForPodTermination {
    param (
        [string]$PodName,
        [string]$Namespace = "default",
        [string]$KubeConfig,
        [int]$TimeoutSeconds = 300,
        [int]$PollIntervalSeconds = 5
    )

    $startTime = Get-Date
    while ($true) {
        $pod = kubectl get pod $PodName -n $Namespace -o json --kubeconfig $KubeConfig | ConvertFrom-Json
        $phase = $pod.status.phase

        if ($phase -eq "Succeeded") {
            Write-Host "Pod has terminated with status: $phase"
            break
        }

        if ($phase -eq "Failed") {
            Write-Host -ForegroundColor Red "Pod has terminated with status: $phase"
            throw "Pod has terminated with status: $phase"
        }

        $elapsed = (Get-Date) - $startTime
        if ($elapsed.TotalSeconds -ge $TimeoutSeconds) {
            Write-Host -ForegroundColor Red "Timeout waiting for pod '$PodName' to terminate."
            throw "Timeout waiting for pod '$PodName' to terminate."
        }

        Write-Host "Waiting for pod '$PodName' to terminate... Current phase: $phase"
        Start-Sleep -Seconds $PollIntervalSeconds
    }
}

function Check-ContainerExitCodes {
    param (
        [string]$PodName,
        [string]$Namespace = "default",
        [string]$KubeConfig
    )

    $notSucceeded = @()
    $pod = kubectl get pod $PodName -n $Namespace -o json --kubeconfig $KubeConfig | ConvertFrom-Json
    $allSucceeded = $true

    # --- Check init containers ---
    $initStatuses = $pod.status.initContainerStatuses
    foreach ($container in $initStatuses) {
        $name = $container.name
        $state = $container.state

        if ($state.terminated) {
            $exitCode = $state.terminated.exitCode
            Write-Host "Init container '$name' exited with code $exitCode"

            if ($exitCode -ne 0 -and $exitCode -ne 137) {
                $allSucceeded = $false
                $notSucceeded += $name
            }
        }
        elseif ($state.running) {
            Write-Host "Init container '$name' is still running."
            $allSucceeded = $false
            $notSucceeded += $name
        }
        else {
            Write-Host "Init container '$name' is in an unexpected state."
            $allSucceeded = $false
            $notSucceeded += $name
        }
    }

    # --- Check main containers ---
    $containerStatuses = $pod.status.containerStatuses
    foreach ($container in $containerStatuses) {
        $name = $container.name
        $state = $container.state

        if ($state.terminated) {
            $exitCode = $state.terminated.exitCode
            Write-Host "Container '$name' exited with code $exitCode"

            if ($exitCode -ne 0) {
                $allSucceeded = $false
                $notSucceeded += $name
            }
        }
        elseif ($state.running) {
            Write-Host "Container '$name' is still running."
            $allSucceeded = $false
            $notSucceeded += $name
        }
        else {
            Write-Host "Container '$name' is in an unexpected state."
            $allSucceeded = $false
            $notSucceeded += $name
        }
    }

    if ($allSucceeded) {
        Write-Host "All containers exited successfully."
    }
    else {
        Write-Host "One or more containers did not exit cleanly: " + $notSucceeded
        throw "One or more containers did not exit cleanly."
    }
}

# Step 1: Ensure that the pod terminates as we don't want to leave the pod running and consuming resources.
Wait-ForPodTermination `
    -PodName $PodName `
    -Namespace $Namespace `
    -TimeoutSeconds $TimeoutSeconds `
    -PollIntervalSeconds $PollIntervalSeconds `
    -KubeConfig $KubeConfig

# Step 2: Check that each container exited gracefully with exit code 0.
# Containers are supposed to handle SIGTERM gracefully so a non-zero exit code would indicate an issue.
Check-ContainerExitCodes `
    -PodName $PodName `
    -Namespace $Namespace `
    -KubeConfig $KubeConfig
