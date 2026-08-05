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

# Build the tsp-codegen Docker image if it doesn't exist
if ($forceBuild -or -not (docker images -q tsp-codegen)) {
    pwsh $root/src/tools/tsp-codegen/build-tsp-codegen.ps1
}

# Build the python-linter Docker image if it doesn't exist
if ($forceBuild -or -not (docker images -q python-linter)) {
    pwsh $root/src/tools/python-linter/build-python-linter.ps1
}

$typespecBase = "$root/src/specifications/typespec"
$specs = @{
    "cleanroom-governance-service" = @{
        "js"     = @{
            "src" = "server/nodejs/src/generated";
            "dst" = "$root/src/governance/ccf-app/js/src/auto-generated";
        };
        "csharp" = @{
            "src" = "client/csharp/src/Generated";
            "dst" = "$root/src/sdk/clients/cgs-client/auto-generated";
        };
    };
    # "cleanroom-sdk"                = @{
    #     "csharp" = @{
    #         "src" = "server/aspnet/generated";
    #         "dst" = "$root/src/sdk/dotnet/auto-generated/cleanroom-sdk";
    #     }
    # };
    "ccf"                          = @{
        "csharp" = @{
            "src" = "client/csharp/src/Generated";
            "dst" = "$root/src/sdk/clients/ccf-client/auto-generated";
        };
    };
    # "cleanroom-governance-client-proxy" = @{
    #     "csharp" = @{
    #         "src" = "server/aspnet";
    #         "dst" = "$root/src/sdk/cleanroom-governance-client-proxy/service";
    #     };
    #     "python" = @{
    #         "src" = "clients/python";
    #         "dst" = "$root/src/sdk/cleanroom-governance-client-proxy/client/python";
    #     };
    # };
}

# Invoke script to emit operations Typespec from app.json.
$pythonScript = Join-Path $PSScriptRoot "generate_typespec.py"

$pythonArgs = @()
$pythonArgs += "--app-json"
$pythonArgs += "$root/src/governance/ccf-app/js/app.json"
$pythonArgs += "--output-dir"
$pythonArgs += "$root/src/specifications/typespec"

python3 $pythonScript @pythonArgs

# Iterate over specifications to generate code for each target.
$specs.GetEnumerator() | ForEach-Object {
    $spec = $_.Key
    $typespecFolder = "$typespecBase/$spec"
    Write-Host "========================================="
    Write-Host "Generating code for typespec in folder '$typespecFolder'"

    if ($clean) {
        rm -fr "$typespecFolder/node_modules"
        rm -fr "$typespecFolder/generated"
    }

    # Typespec model generation
    $uid = id -u ${env:USER}
    $gid = id -g ${env:USER}
    docker run `
        -e TSP_FOLDER=/tsp/$spec `
        -v ${typespecBase}:/tsp `
        -u ${uid}:${gid} `
        --name tsp-codegen `
        --rm tsp-codegen

    $targets = $_.Value
    $targets.GetEnumerator() | ForEach-Object {
        $language = $_.Key
        $src = "$typespecFolder/generated/$($_.Value.src)/."

        $dst = $_.Value.dst
        rm -fr $dst
        mkdir -p $dst

        Write-Host "Copying emitted code from '$src' to '$dst'"
        cp -r $src $dst

        if ($language -eq "python") {
            Write-Host "Formatting Python code in '$dst' using isort and black"
            docker run `
                -v ${dst}:/src `
                -u ${uid}:${gid} `
                --name python-linter `
                --rm python-linter src
        }
    }
}


