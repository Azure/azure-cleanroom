# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Generate TPC-DS data on an Azure VM and upload to publisher/consumer storage accounts.

.PARAMETER scaleFactor
    TPC-DS scale factor (default: 1000 -> ~1 TB).

.PARAMETER publisherStorageAccount
    Publisher Azure Storage account name.

.PARAMETER consumerStorageAccount
    Consumer Azure Storage account name.

.PARAMETER vmResourceGroup
    Azure resource group for the temporary VM (created if needed).

.PARAMETER location
    Azure region for the VM (default: centralindia).

.PARAMETER vmSize
    Azure VM size (default: Standard_D32s_v5).

.PARAMETER dataDiskSizeGb
    Data disk size in GB (default: 3072).

.PARAMETER skipVmDeletion
    If set, do not delete the VM after data generation.

.PARAMETER dataFormats
    Comma-separated list of formats to generate (default: "csv,parquet").

.NOTES
    Data generation runs entirely on the VM via cloud-init. A systemd unit
    (tpcds-datagen.service, TimeoutStartSec=0) executes the vm_scripts/*.sh
    steps in order on first boot; a second unit (tpcds-export-status.service,
    ordered After the datagen service) always runs afterwards - on success or
    failure - to upload the per-step logs and write a status blob (status.json)
    to a transient container. This host script stages inputs, creates the VM
    with that cloud-init as custom data, then polls the status blob for a
    well-defined success/failure result.
#>
[CmdletBinding()]
param(
    [int]$scaleFactor = 1000,

    [string]$publisherStorageAccount = "avwgndilajulqsa",

    [string]$consumerStorageAccount = "nldjeffcxauamsa",

    [string]$publisherResourceGroup = "cl-ob-publisher-tpcds-analytics",

    [string]$consumerResourceGroup = "cl-ob-consumer-tpcds-analytics",

    [string]$vmResourceGroup = "tpcds-datagen-rg",

    [string]$location = "centralindia",

    [string]$vmSize = "Standard_D32s_v5",

    [int]$dataDiskSizeGb = 3072,

    [string]$dataFormats = "csv,parquet",

    [int]$parallelWorkers = 8,

    [switch]$skipVmDeletion
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# The default 3072 GB disk holds ~1 TB csv + parquet + toolkit for sf1000. A
# larger scale factor (e.g. sf2000 ~2 TB csv) needs a proportionally bigger disk,
# so when the caller did not pin -dataDiskSizeGb, auto-size it from the scale
# factor (~3 GB/sf, rounded up to a 1024 GB boundary). Never shrinks below the
# default, so small scale factors are unaffected (sf1000 stays 3072).
if (-not $PSBoundParameters.ContainsKey('dataDiskSizeGb')) {
    $scaledDiskGb = [int]([math]::Ceiling(($scaleFactor * 3.0) / 1024.0) * 1024)
    if ($scaledDiskGb -gt $dataDiskSizeGb) {
        Write-Host ("Auto-sizing data disk {0} GB -> {1} GB for scaleFactor={2}." -f `
            $dataDiskSizeGb, $scaledDiskGb, $scaleFactor)
        $dataDiskSizeGb = $scaledDiskGb
    }
}

$stressTestDir = (Get-Item $PSScriptRoot).Parent.FullName
$tableConfigPath = Join-Path $stressTestDir "fixtures/table-partition-config.json"
$tableConfig = Get-Content -Raw -Path $tableConfigPath | ConvertFrom-Json
$publisherTables = $tableConfig.publisher.tables
$consumerTables = $tableConfig.consumer.tables
$formats = $dataFormats -split "," |
    ForEach-Object { $_.Trim().Trim('"', "'") } |
    Where-Object { $_ -ne "" }

$vmName = "tpcds-datagen-vm"
$vmScriptsDir = Join-Path $PSScriptRoot "vm_scripts"
# Per-scale-factor transient containers so multiple scale factors can generate
# in parallel without clobbering each other's status blob / toolkit staging.
$toolkitContainer = "tpcds-toolkit-sf$scaleFactor"
$statusContainer = "tpcds-status-sf$scaleFactor"
$statusBlob = "status.json"

# Ordered data-generation steps; each maps to vm_scripts/<step>.sh.
$steps = @("setup", "build", "dsdgen", "merge", "parquet", "csv-convert", "upload")

function Invoke-AzQuiet {
    # Run a block (typically az CLI calls) with throw-on-nonzero-exit
    # temporarily disabled. Returns whatever the block emits, so callers can
    # capture output: $val = Invoke-AzQuiet { az ... }
    param([Parameter(Mandatory)][scriptblock]$ScriptBlock)
    $prev = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    try { & $ScriptBlock } finally { $PSNativeCommandUseErrorActionPreference = $prev }
}

function Convert-Jinja {
    # Render a Jinja2 template file to a string using python3. The cloud-init
    # bash/systemd templates live as reviewable static files under vm_scripts/
    # (rather than inline here-strings); values are passed as a JSON context on
    # stdin so the template stays free of PowerShell string interpolation.
    param(
        [Parameter(Mandatory)][string]$TemplatePath,
        [hashtable]$Variables = @{}
    )

    if (-not (Test-Path $TemplatePath)) {
        throw "Template not found: $TemplatePath"
    }

    if (-not $script:JinjaChecked) {
        Invoke-AzQuiet { python3 -c "import jinja2" 2>$null } | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Rendering the cloud-init templates requires python3 with the " +
                "jinja2 package. Activate the workspace venv (uv sync) or run " +
                "'python3 -m pip install jinja2'."
        }
        $script:JinjaChecked = $true
    }

    $renderScript = @'
import json, os, sys

from jinja2 import Environment, FileSystemLoader, StrictUndefined

template_path, output_path = sys.argv[1], sys.argv[2]
context = json.loads(sys.stdin.read() or "{}")
env = Environment(
    loader=FileSystemLoader(os.path.dirname(template_path) or "."),
    undefined=StrictUndefined,
    keep_trailing_newline=True,
    autoescape=False,
    trim_blocks=True,
    lstrip_blocks=True,
    # The bash templates are pulled in with {% include %}; bash array-length
    # syntax (e.g. ${#arr[@]}) would otherwise be parsed as a Jinja comment.
    # None of our templates use Jinja comments, so move the delimiters away
    # from the default '{#'/'#}' to keep includes verbatim.
    comment_start_string="{##",
    comment_end_string="##}",
)
template = env.get_template(os.path.basename(template_path))
with open(output_path, "w", encoding="utf-8") as handle:
    handle.write(template.render(**context))
'@

    $ctxJson = $Variables | ConvertTo-Json -Compress -Depth 5
    $outFile = New-TemporaryFile
    try {
        Invoke-AzQuiet {
            $ctxJson | python3 -c $renderScript $TemplatePath $outFile.FullName
        } | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Jinja rendering failed for $TemplatePath"
        }
        return [System.IO.File]::ReadAllText($outFile.FullName)
    }
    finally {
        Remove-Item $outFile.FullName -Force -ErrorAction SilentlyContinue
    }
}

function New-TpcdsCloudInit {
    <#
    .SYNOPSIS
        Render a #cloud-config that runs the TPC-DS data-generation steps once
        on first boot and, separately, exports logs and a status blob.
    .DESCRIPTION
        Renders vm_scripts/cloud-init.yaml.j2, which owns the whole cloud-init
        layout and pulls in the per-step scripts, the tpcds-datagen.sh runner
        and its service (TimeoutStartSec=0 so the multi-hour run is not killed),
        and the tpcds-export-status.sh exporter and its service via Jinja
        {% include %}. The exporter is ordered After the datagen service via
        Wants/After so it always runs - success or failure - to upload the
        per-step logs and write status.json. It uses curl+SAS with an azcopy
        (MSI) fallback.
    #>
    param(
        [Parameter(Mandatory)][hashtable]$Env,
        [Parameter(Mandatory)][string[]]$Steps,
        [Parameter(Mandatory)][string]$StatusSasUrl
    )

    return Convert-Jinja `
        -TemplatePath (Join-Path $vmScriptsDir "cloud-init.yaml.j2") `
        -Variables @{
            env            = $Env
            step_list      = $Steps
            steps          = ($Steps -join ' ')
            status_sas_url = $StatusSasUrl
        }
}

function Wait-TpcdsStatus {
    <#
    .SYNOPSIS
        Poll the status blob written by the VM's cloud-init orchestrator and
        return on success or throw (surfacing the failing step's log) on error.
    #>
    param(
        [Parameter(Mandatory)][string]$Account,
        [Parameter(Mandatory)][string]$Container,
        [string]$Blob = "status.json",
        [int]$TimeoutHours = 26
    )

    Write-Host "  Waiting for data-generation status blob ($Container/$Blob)..." `
        -ForegroundColor Yellow
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddHours($TimeoutHours)
    $lastLogMinute = -1

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 60

        $exists = Invoke-AzQuiet {
            az storage blob exists `
                --account-name $Account `
                --container-name $Container `
                --name $Blob `
                --auth-mode login `
                --query "exists" `
                --output tsv 2>$null
        }

        $elapsedMinutes = [math]::Floor($sw.Elapsed.TotalMinutes)
        if ($elapsedMinutes -ge $lastLogMinute + 30) {
            Write-Host "  [$($sw.Elapsed.ToString('hh\:mm\:ss'))] still generating..."
            $lastLogMinute = $elapsedMinutes
        }

        if ("$exists".Trim().ToLower() -ne "true") { continue }

        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) `
            "tpcds-status-$([System.Guid]::NewGuid().ToString('N')).json"
        # A transient download failure (network/RBAC) after 'exists' returned
        # true would otherwise leave a missing/partial file and surface as a
        # misleading JSON/IO error. Run under Invoke-AzQuiet and check the az
        # exit code; on failure, skip this poll and retry on the next iteration
        # instead of parsing.
        Invoke-AzQuiet {
            az storage blob download `
                --account-name $Account `
                --container-name $Container `
                --name $Blob `
                --auth-mode login `
                --file $tmp `
                --output none 2>$null
        }
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -Path $tmp)) {
            Write-Host "  Status blob download failed transiently; retrying..." `
                -ForegroundColor Yellow
            Remove-Item -Path $tmp -Force -ErrorAction SilentlyContinue
            continue
        }
        $status = Get-Content -Raw -Path $tmp | ConvertFrom-Json
        Remove-Item -Path $tmp -Force -ErrorAction SilentlyContinue

        if ($status.status -eq "completed") {
            Write-Host "  Data generation completed (elapsed $($sw.Elapsed.ToString('hh\:mm\:ss')))." `
                -ForegroundColor Green
            return
        }

        # Best-effort: surface the tail of the failing step's log.
        if ($status.step) {
            $logExists = Invoke-AzQuiet {
                az storage blob exists `
                    --account-name $Account `
                    --container-name $Container `
                    --name "$($status.step).log" `
                    --auth-mode login `
                    --query "exists" `
                    --output tsv 2>$null
            }
            if ("$logExists".Trim().ToLower() -eq "true") {
                $logTmp = Join-Path ([System.IO.Path]::GetTempPath()) "tpcds-$($status.step).log"
                az storage blob download `
                    --account-name $Account `
                    --container-name $Container `
                    --name "$($status.step).log" `
                    --auth-mode login `
                    --file $logTmp `
                    --output none 2>$null
                Write-Host "=== $($status.step) log (tail) ===" -ForegroundColor Yellow
                Get-Content -Path $logTmp -Tail 60 | ForEach-Object { Write-Host $_ }
                Remove-Item -Path $logTmp -Force -ErrorAction SilentlyContinue
            }
        }

        $detail = if ($status.detail) { $status.detail } else { "see cloud-init logs on the VM" }
        throw "TPC-DS data generation failed at step '$($status.step)': $detail"
    }

    throw "Timed out after ${TimeoutHours}h waiting for TPC-DS data generation to finish."
}

Write-Host "=== TPC-DS Data Generation on Azure VM ===" -ForegroundColor Cyan
$startTime = Get-Date
Write-Host "  Start Time:         $($startTime.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "  Scale Factor:       $scaleFactor (~$scaleFactor GB)"
Write-Host "  VM Resource Group:  $vmResourceGroup"
Write-Host "  Location:           $location"
Write-Host "  VM Size:            $vmSize"
Write-Host "  Data Disk:          ${dataDiskSizeGb} GB"
Write-Host "  Publisher Storage:  $publisherStorageAccount"
Write-Host "  Consumer Storage:   $consumerStorageAccount"
Write-Host "  Formats:            $($formats -join ', ')"

Write-Host "`n=== Step 1/5: Creating VM resource group ===" -ForegroundColor Cyan
az group create --name $vmResourceGroup --location $location --output none

function Confirm-StorageAccount {
    param(
        [Parameter(Mandatory)][string]$ResourceGroup,
        [Parameter(Mandatory)][string]$StorageAccount,
        [Parameter(Mandatory)][string]$Location
    )

    $rgExists = Invoke-AzQuiet {
        az group show --name $ResourceGroup --query "name" -o tsv 2>$null
    }
    if (-not $rgExists) {
        Write-Host "  Creating resource group: $ResourceGroup" -ForegroundColor Yellow
        az group create --name $ResourceGroup --location $Location --output none
    }

    $saExists = Invoke-AzQuiet {
        az storage account show `
            --name $StorageAccount `
            --resource-group $ResourceGroup `
            --query "name" -o tsv 2>$null
    }
    if (-not $saExists) {
        Write-Host "  Creating storage account: $StorageAccount in $ResourceGroup" `
            -ForegroundColor Yellow
        az storage account create `
            --name $StorageAccount `
            --resource-group $ResourceGroup `
            --location $Location `
            --sku Standard_LRS `
            --kind StorageV2 `
            --allow-blob-public-access false `
            --output none

        $userObjectId = az ad signed-in-user show --query id -o tsv 2>$null
        if ($userObjectId) {
            $saId = az storage account show `
                --name $StorageAccount `
                --resource-group $ResourceGroup `
                --query "id" -o tsv
            Invoke-AzQuiet {
                az role assignment create `
                    --assignee-object-id $userObjectId `
                    --assignee-principal-type User `
                    --role "Storage Blob Data Contributor" `
                    --scope $saId `
                    --output none 2>$null
            }
        }
    }
}

Confirm-StorageAccount -ResourceGroup $publisherResourceGroup `
    -StorageAccount $publisherStorageAccount -Location $location
Confirm-StorageAccount -ResourceGroup $consumerResourceGroup `
    -StorageAccount $consumerStorageAccount -Location $location

Write-Host "=== Step 2/5: Creating blob containers ($parallelWorkers parallel) ===" -ForegroundColor Cyan
$containerDefs = @()
foreach ($format in $formats) {
    foreach ($table in $publisherTables) {
        $tableSafe = $table.Replace("_", "-")
        $containerDefs += @{
            Name    = "tpcds-pub-${tableSafe}-sf${scaleFactor}-${format}"
            Account = $publisherStorageAccount
        }
    }
    foreach ($table in $consumerTables) {
        $tableSafe = $table.Replace("_", "-")
        $containerDefs += @{
            Name    = "tpcds-con-${tableSafe}-sf${scaleFactor}-${format}"
            Account = $consumerStorageAccount
        }
    }
}
Write-Host "  Creating $($containerDefs.Count) containers..."
$containerDefs | ForEach-Object -ThrottleLimit $parallelWorkers -Parallel {
    $def = $_
    az storage container create `
        --name $def.Name `
        --account-name $def.Account `
        --auth-mode login `
        --output none 2>$null
    Write-Host "  Created $($def.Name)"
}

Write-Host "=== Step 3/5: Staging TPC-DS toolkit and helper scripts ===" -ForegroundColor Cyan

# SSH is blocked; stage the toolkit + helpers to blob storage so cloud-init can
# pull them on the VM. Done before VM creation so the whole run is a single
# first-boot execution with no host-in-the-loop steps.
$tpcdsToolkitDir = Join-Path (Split-Path $stressTestDir) "DSGen-software-code-4.0.0"
if (-not (Test-Path -Path $tpcdsToolkitDir)) {
    throw ("TPC-DS toolkit not found at $tpcdsToolkitDir. " +
        "Download 'TPC-DS Tools' from " +
        "https://www.tpc.org/tpc_documents_current_versions/current_specifications5.asp " +
        "(TPC license registration required) and unzip into that path.")
}

Invoke-AzQuiet {
    az storage container create `
        --name $toolkitContainer `
        --account-name $publisherStorageAccount `
        --auth-mode login `
        --output none 2>$null
    az storage container create `
        --name $statusContainer `
        --account-name $publisherStorageAccount `
        --auth-mode login `
        --output none 2>$null
}

$toolkitTarPath = Join-Path ([System.IO.Path]::GetTempPath()) "tpcds-toolkit.tar.gz"
Write-Host "  Creating toolkit tarball..."
tar -czf $toolkitTarPath -C (Split-Path $stressTestDir) "DSGen-software-code-4.0.0"
$tarBytes = (Get-Item $toolkitTarPath).Length
Write-Host "  Toolkit tarball size: $tarBytes bytes"
if ($tarBytes -lt 1MB) {
    throw ("Toolkit tarball is only $tarBytes bytes. Source dir " +
        "$(Split-Path $stressTestDir)/DSGen-software-code-4.0.0 appears empty.")
}

$converterScript = Join-Path $PSScriptRoot "convert_to_parquet.py"
$helpersScript = Join-Path $stressTestDir "lib/tpcds_helpers.py"
$formatUtilsScript = Join-Path $stressTestDir "lib/format_utils.py"

Write-Host "  Uploading toolkit and helpers to staging storage..."
az storage blob upload `
    --account-name $publisherStorageAccount `
    --container-name $toolkitContainer `
    --name "tpcds-toolkit.tar.gz" `
    --file $toolkitTarPath `
    --auth-mode login `
    --overwrite `
    --output none
az storage blob upload `
    --account-name $publisherStorageAccount `
    --container-name $toolkitContainer `
    --name "convert_to_parquet.py" `
    --file $converterScript `
    --auth-mode login `
    --overwrite `
    --output none
az storage blob upload `
    --account-name $publisherStorageAccount `
    --container-name $toolkitContainer `
    --name "tpcds_helpers.py" `
    --file $helpersScript `
    --auth-mode login `
    --overwrite `
    --output none
az storage blob upload `
    --account-name $publisherStorageAccount `
    --container-name $toolkitContainer `
    --name "format_utils.py" `
    --file $formatUtilsScript `
    --auth-mode login `
    --overwrite `
    --output none

Remove-Item -Path $toolkitTarPath -Force -ErrorAction SilentlyContinue

# User-delegation SAS for the status container: the VM's cloud-init orchestrator
# uses it (via curl) to write status.json and step logs. curl is available from
# first boot, before azcopy is installed by setup.sh.
$statusExpiry = (Get-Date).AddHours(72).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$statusSasToken = az storage container generate-sas `
    --account-name $publisherStorageAccount `
    --name $statusContainer `
    --permissions rcw `
    --expiry $statusExpiry `
    --auth-mode login `
    --as-user `
    --output tsv
if (-not $statusSasToken) { throw "Failed to generate status container SAS." }
$statusSasUrl = "https://$publisherStorageAccount.blob.core.windows.net/$statusContainer" +
    "?$statusSasToken"

# Env consumed by the vm_scripts/*.sh steps (written to /etc/tpcds/tpcds.env).
$publisherTablesStr = $publisherTables -join " "
$consumerTablesStr = $consumerTables -join " "
$formatsStr = $formats -join " "
$stepEnv = @{
    STORAGE_ACCOUNT           = $publisherStorageAccount
    TOOLKIT_CONTAINER         = $toolkitContainer
    SCALE_FACTOR              = "$scaleFactor"
    PARALLEL_WORKERS          = "$parallelWorkers"
    PUBLISHER_TABLES          = $publisherTablesStr
    CONSUMER_TABLES           = $consumerTablesStr
    FORMATS                   = $formatsStr
    PUBLISHER_STORAGE_ACCOUNT = $publisherStorageAccount
    CONSUMER_STORAGE_ACCOUNT  = $consumerStorageAccount
}

$cloudInit = New-TpcdsCloudInit -Env $stepEnv -Steps $steps -StatusSasUrl $statusSasUrl
$cloudInit = $cloudInit -replace "`r`n", "`n"

# Fail fast on an empty/degenerate render. A 'python3' that is a Windows .cmd
# shim mangles the multi-line '-c' render script (cmd %* drops everything after
# the first newline), so Jinja writes nothing yet exits 0 - producing an empty
# cloud-init that the VM silently ignores (orchestrator never runs, no status
# blob). 'python3' must be a real interpreter (a python3.exe) with jinja2.
if ([string]::IsNullOrWhiteSpace($cloudInit) -or $cloudInit.Length -lt 200) {
    throw ("Rendered cloud-init is empty or too small ($($cloudInit.Length) " +
        "bytes). Jinja rendering via 'python3' likely failed silently. Ensure " +
        "'python3' resolves to a real python3.exe (not a .cmd/.bat shim) with " +
        "the jinja2 package installed.")
}

# az passes --custom-data through latin-1; a stray non-ASCII char (e.g. an
# em-dash pasted into a vm_script or comment) would otherwise fail deep inside
# az with an opaque codec error. Surface it here with the offending line.
$nonAscii = [regex]::Match($cloudInit, "[^\x00-\x7F]")
if ($nonAscii.Success) {
    $lineNo = ($cloudInit.Substring(0, $nonAscii.Index) -split "`n").Count
    throw ("cloud-init contains a non-ASCII character (U+{0:X4}) on line {1}; " +
        "az --custom-data only accepts ASCII. Replace it (e.g. use '-' not an " +
        "em-dash) in the vm_scripts or generated content." -f `
        [int][char]$nonAscii.Value, $lineNo)
}

$cloudInitPath = Join-Path ([System.IO.Path]::GetTempPath()) "tpcds-cloud-init.yaml"
[System.IO.File]::WriteAllText($cloudInitPath, $cloudInit)

Write-Host "=== Step 4/5: Creating data-generation VM ===" -ForegroundColor Cyan

# cloud-init only runs on first boot, so an existing VM must be recreated.
$vmExists = Invoke-AzQuiet {
    az vm show --resource-group $vmResourceGroup --name $vmName --query "name" -o tsv 2>$null
}
if ($vmExists) {
    Write-Host "  Existing VM found; deleting so cloud-init runs on a fresh boot..." `
        -ForegroundColor Yellow
    az vm delete `
        --resource-group $vmResourceGroup `
        --name $vmName `
        --yes --force-deletion yes --output none
}

az vm create `
    --resource-group $vmResourceGroup `
    --name $vmName `
    --image Ubuntu2404 `
    --size $vmSize `
    --data-disk-sizes-gb $dataDiskSizeGb `
    --storage-sku Premium_LRS `
    --admin-username azureuser `
    --generate-ssh-keys `
    --assign-identity "[system]" `
    --custom-data $cloudInitPath `
    --output none

Write-Host "  Created VM: $vmName" -ForegroundColor Green
Remove-Item -Path $cloudInitPath -Force -ErrorAction SilentlyContinue

$vmPrincipalId = az vm show --resource-group $vmResourceGroup --name $vmName `
    --query "identity.principalId" -o tsv

$pubSaId = az storage account show `
    --name $publisherStorageAccount `
    --resource-group $publisherResourceGroup `
    --query "id" -o tsv
$conSaId = az storage account show `
    --name $consumerStorageAccount `
    --resource-group $consumerResourceGroup `
    --query "id" -o tsv

# The step scripts (build.sh toolkit pull, upload.sh writes) use the VM's
# managed identity for storage. Roles are granted now, right after creation;
# they have propagated well before those steps run (setup + build come first,
# and build.sh retries azcopy to absorb any residual propagation lag).
Write-Host "  Assigning Storage Blob Data Contributor to VM identity..."
Invoke-AzQuiet {
    az role assignment create `
        --assignee-object-id $vmPrincipalId `
        --assignee-principal-type ServicePrincipal `
        --role "Storage Blob Data Contributor" `
        --scope $pubSaId `
        --output none 2>$null
    az role assignment create `
        --assignee-object-id $vmPrincipalId `
        --assignee-principal-type ServicePrincipal `
        --role "Storage Blob Data Contributor" `
        --scope $conSaId `
        --output none 2>$null
}
Write-Host "  Role assignments created." -ForegroundColor Green

Write-Host "=== Step 5/5: Generating data on VM (SF=$scaleFactor) ===" -ForegroundColor Cyan
Write-Host "  cloud-init runs: setup -> build -> dsdgen -> merge -> parquet -> csv-convert -> upload."
Write-Host "  This can take many hours for large scale factors..."

Wait-TpcdsStatus -Account $publisherStorageAccount -Container $statusContainer -Blob $statusBlob

Write-Host "  Data generated and uploaded to storage accounts." -ForegroundColor Green

Invoke-AzQuiet {
    az storage container delete `
        --name $toolkitContainer `
        --account-name $publisherStorageAccount `
        --auth-mode login `
        --output none 2>$null
    az storage container delete `
        --name $statusContainer `
        --account-name $publisherStorageAccount `
        --auth-mode login `
        --output none 2>$null
}

if (-not $skipVmDeletion) {
    Write-Host "`n=== Cleaning up VM ===" -ForegroundColor Cyan
    az vm delete `
        --resource-group $vmResourceGroup `
        --name $vmName `
        --yes --force-deletion yes --output none
    az group delete --name $vmResourceGroup --yes --no-wait --output none
    Write-Host "  VM deleted: $vmName" -ForegroundColor Green
}
else {
    Write-Host "`n  VM kept: $vmName"
    Write-Host "  Delete manually: az group delete -n $vmResourceGroup --yes"
}

Write-Host "`n=== TPC-DS data generation complete ===" -ForegroundColor Green
$endTime = Get-Date
$totalElapsed = $endTime - $startTime
Write-Host "  Start Time:         $($startTime.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "  End Time:           $($endTime.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "  Total Duration:     $($totalElapsed.ToString('hh\:mm\:ss'))"
Write-Host "  Scale Factor: $scaleFactor"
foreach ($format in $formats) {
    Write-Host "  Publisher $format tables:" -ForegroundColor Green
    foreach ($table in $publisherTables) {
        $tableSafe = $table.Replace("_", "-")
        Write-Host "    $publisherStorageAccount/tpcds-pub-${tableSafe}-sf${scaleFactor}-${format}"
    }
    Write-Host "  Consumer  $format tables:" -ForegroundColor Green
    foreach ($table in $consumerTables) {
        $tableSafe = $table.Replace("_", "-")
        Write-Host "    $consumerStorageAccount/tpcds-con-${tableSafe}-sf${scaleFactor}-${format}"
    }
}
