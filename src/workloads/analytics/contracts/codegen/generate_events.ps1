param(
    [parameter(Mandatory = $false)]
    [switch]$forceBuild,

    [parameter(Mandatory = $false)]
    [switch]$clean
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

# Build the python-linter Docker image if it doesn't exist
if ($forceBuild -or -not (docker images -q python-linter)) {
    pwsh $root/src/tools/python-linter/build-python-linter.ps1
}

$specBase = "$root/src/workloads/analytics/contracts"
$specs = @{
    "operational_events"      = @{
        "src" = "src/analytics_contracts/events/operational_events.json";
        "dst" = "$root/src/workloads/analytics/contracts/src/analytics_contracts/events";
        "outfile" = "operational_events_factory.py";
    };
    "audit_records"   = @{
        "src" = "src/analytics_contracts/audit/audit_records.json";
        "dst" = "$root/src/workloads/analytics/contracts/src/analytics_contracts/audit";
        "outfile" = "audit_records_factory.py";
    };
    "statistics_events" = @{
        "src" = "src/analytics_contracts/statistics/statistics_events.json";
        "dst" = "$root/src/workloads/analytics/contracts/src/analytics_contracts/statistics";
        "outfile" = "statistics_events_factory.py";
        };
    };


$specs.GetEnumerator() | ForEach-Object {
    $spec = $_.Key
    $specFolder = "$specBase/$spec"
    Write-Host "========================================="
    Write-Host "Generating code for $spec events in folder '$specFolder'"
    $dst = $($_.Value.dst)
    $dstFile = "$dst/$($_.Value.outfile)"
    python3 $PSScriptRoot/generator.py --type $spec --input $($_.Value.src) --output $dstFile

    $uid = id -u ${env:USER}
    $gid = id -g ${env:USER}
    Write-Host "Formatting Python code in '$dst' using isort and black"
    docker run `
        -v ${dst}:/src `
        -u ${uid}:${gid} `
        --name python-linter `
        --rm python-linter src
}


