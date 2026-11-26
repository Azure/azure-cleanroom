[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module powershell-yaml
$Root = Split-Path $PSScriptRoot -Parent
$Workflow = Get-Content "$Root/.github/workflows/official_build.yml" -Raw | ConvertFrom-Yaml
$Groups = $Workflow.on.workflow_call.inputs.'build-type'.default.Split(',').Trim()
$Outputs = [ordered]@{}

function Add-BuildOutput {
    param([hashtable]$Output)

    $Name = $Output.name
    if ($Outputs.Contains($Name)) {
        if (($Outputs[$Name] | ConvertTo-Json -Compress) -ne ($Output | ConvertTo-Json -Compress)) {
            throw "Conflicting official build definitions for $Name."
        }
        return
    }

    $Outputs[$Name] = $Output
}

function Add-ActionOutputs {
    param([string]$ActionPath)

    $Action = Get-Content "$Root/$ActionPath/action.yml" -Raw | ConvertFrom-Yaml
    switch (Split-Path $ActionPath -Leaf) {
        'build-cgs-js-app' {
            Add-BuildOutput @{ name = 'cgs-js-app'; kind = 'cgs'; path = ''; dockerfile = '' }
            return
        }
        'build-cgs-constitution' {
            Add-BuildOutput @{ name = 'cgs-constitution'; kind = 'constitution'; path = '';
                dockerfile = '' }
            return
        }
        'build-azcli' {
            Add-BuildOutput @{ name = 'cleanroom-cli'; kind = 'cli'; path = ''; dockerfile = '' }
            return
        }
    }

    foreach ($Step in $Action.runs.steps) {
        switch ($Step.uses) {
            './.github/actions/build-container-and-artefacts' {
                if ($Step.with.'build-args') {
                    throw "Build arguments need explicit support for $($Step.with.name)."
                }
                $Context = $Step.with.context.Replace('${{ github.workspace }}', '.')
                Add-BuildOutput @{ name = $Step.with.name; kind = 'container'; path = $Context;
                    dockerfile = $Step.with.dockerfile }
            }
            './.github/actions/build-helm-chart-and-artefacts' {
                $ChartPath = $Step.with.path.Replace('${{ github.workspace }}', '.')
                Add-BuildOutput @{ name = "$($Step.with.name)-helm"; kind = 'helm';
                    path = $ChartPath; dockerfile = '' }
            }
            default {
                throw "Unsupported official build step in ${ActionPath}: $($Step | ConvertTo-Json)."
            }
        }
    }
}

foreach ($Step in $Workflow.jobs.build.steps) {
    if ($Step.uses -notlike './.github/actions/build-*') {
        continue
    }
    if ($Step.if) {
        $Match = [regex]::Match($Step.if, "contains\(inputs\.build-type, '([^']+)'\)")
        if (-not $Match.Success) {
            throw "Unsupported official build condition: $($Step.if)"
        }
        if ($Match.Groups[1].Value -notin $Groups) {
            continue
        }
    }
    Add-ActionOutputs $Step.uses
}

if ($Outputs.Count -eq 0) {
    throw 'The official build plan contains no outputs.'
}

@{ include = @($Outputs.Values) } | ConvertTo-Json -Depth 10 -Compress
