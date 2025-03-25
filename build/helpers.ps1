function Get-Digest {
    [CmdletBinding()]
    param (
        [Parameter(Mandatory = $true)]
        [string]$repo,

        [Parameter(Mandatory = $true)]
        [string]$containerName,

        [Parameter(Mandatory = $true)]
        [string]$tag
    )

    $manifest = oras manifest fetch $repo/"$containerName":$tag

    $manifestRaw = ""
    foreach ($line in $manifest) {
        $manifestRaw += $line + "`n"
    }
    $shaGenerator = [System.Security.Cryptography.SHA256]::Create()
    $manifestRaw = $manifestRaw.TrimEnd("`n")
    $shaBytes = $shaGenerator.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($manifestRaw))
    $digest = ([System.BitConverter]::ToString($shaBytes) -replace '-').ToLowerInvariant()
    $shaGenerator.Dispose()
    return "sha256:$digest"
}

function Get-Container-Rego-Policy {
    [CmdletBinding()]
    param (
        [Parameter(Mandatory = $true)]
        [string]$repo,

        [Parameter(Mandatory = $true)]
        [string]$containerName,

        [Parameter(Mandatory = $true)]
        [string]$digest,

        [switch]$debugMode,

        [string]$outDir = ""
    )

    if ($outDir -eq "") {
        $outDir = "."
    }

    $containerImage = "${repo}/${containerName}@${digest}"
    $policyJson = Get-Content -Path "$PSScriptRoot/templates/$containerName-policy.json" | ConvertFrom-Json
    $policyJson.containerImage = $containerImage

    $ccePolicyJson = [ordered]@{
        version    = "1.0"
        containers = @($policyJson)
    }

    # Don't remove -Depth 100 or else @() becomes "" and not empty array [] in the json.
    $ccePolicyJson | ConvertTo-Json -Depth 100 | Out-File ${outDir}/${containerName}-ccepolicy-input.json

    if ($debugMode) {
        Write-Host "Generating CCE Policy with --debug-mode parameter"
        az confcom acipolicygen `
            -i ${outDir}/${containerName}-ccepolicy-input.json `
            --debug-mode `
            --outraw `
        | Out-File ${outDir}/${containerName}-ccepolicy-input.rego
    }
    else {
        az confcom acipolicygen `
            -i ${outDir}/${containerName}-ccepolicy-input.json `
            --outraw `
        | Out-File ${outDir}/${containerName}-ccepolicy-input.rego
    }

    # extract the value of the 'containers' variable in the rego by doing an opa eval query whose
    # result is in a JSON format and the "bindings" object in the json will have the containers 
    # property set as an array of strings.
    # example opa eval result value:
    # {
    #   "result": [
    #     {
    #       "expressions": [
    #         {
    #           "value": true,
    #           "text": "data.policy.containers[i].id == \"wnwbyhaxxnxtoacr.azurecr.io/ccr-init@sha256:5dbd9f24ef9eb77a77d234bef8432ae31b33cd13f9ae6d7fd6e848c9e8eb2e9c\"",
    #           "location": {
    #             "row": 1,
    #             "col": 9
    #           }
    #         },
    #         {
    #           "value": true,
    #           "text": "container := data.policy.containers[i]",
    #           "location": {
    #             "row": 1,
    #             "col": 152
    #           }
    #         }
    #       ],
    #       "bindings": {
    #         "container": {
    #           "allow_elevated": true,
    #           "allow_stdio_access": true,
    #           "capabilities": {
    #             "ambient": [],
    #             "bounding": [
    #               "CAP_AUDIT_CONTROL",
    #               "CAP_AUDIT_READ",
    #               "CAP_AUDIT_WRITE",
    #               "CAP_BLOCK_SUSPEND",
    #               "CAP_BPF",
    #               "CAP_CHECKPOINT_RESTORE",
    #               "CAP_CHOWN",
    #               "CAP_DAC_OVERRIDE",
    #               "CAP_DAC_READ_SEARCH",
    #               "CAP_FOWNER",
    #               "CAP_FSETID",
    #               "CAP_IPC_LOCK",
    #               "CAP_IPC_OWNER",
    #               "CAP_KILL",
    #               "CAP_LEASE",
    #               "CAP_LINUX_IMMUTABLE",
    #               "CAP_MAC_ADMIN",
    #               "CAP_MAC_OVERRIDE",
    #               "CAP_MKNOD",
    #               "CAP_NET_ADMIN",
    #               "CAP_NET_BIND_SERVICE",
    #               "CAP_NET_BROADCAST",
    #               "CAP_NET_RAW",
    #               "CAP_PERFMON",
    #               "CAP_SETFCAP",
    #               "CAP_SETGID",
    #               "CAP_SETPCAP",
    #               "CAP_SETUID",
    #               "CAP_SYSLOG",
    #               "CAP_SYS_ADMIN",
    #               "CAP_SYS_BOOT",
    #               "CAP_SYS_CHROOT",
    #               "CAP_SYS_MODULE",
    #               "CAP_SYS_NICE",
    #               "CAP_SYS_PACCT",
    #               "CAP_SYS_PTRACE",
    #               "CAP_SYS_RAWIO",
    #               "CAP_SYS_RESOURCE",
    #               "CAP_SYS_TIME",
    #               "CAP_SYS_TTY_CONFIG",
    #               "CAP_WAKE_ALARM"
    #             ],
    #            },
    #            .
    #            .
    #            .
    #            .
    #            .
    #           "exec_processes": [],
    #           "id": "wnwbyhaxxnxtoacr.azurecr.io/ccr-init@sha256:5dbd9f24ef9eb77a77d234bef8432ae31b33cd13f9ae6d7fd6e848c9e8eb2e9c",
    #           "layers": [
    #             "0e8cfe197af408efc47304e526ec18be9e8229a1ad933e3f3ce6cd3eec1c28f6",
    #             "5cd281bcf7988b78975e713ae7cd1ea272879593fa4117baac4a8a5f8609a079",
    #             "f797b9761716277349981d8626dcfad60e31b002765ba324cce48576f5175141",
    #             "8b4842f06982817534a75bcf71865213b09dfa8313229c384e5201dadbd75e25",
    #             "4b078b98c5f918d1020e8a51a94343b8c79fffc0cb4452c9810d2fa997eb32de",
    #             "068b6d665ac86dc2268de015a9db10f62b188440ad6dc971bf6ff8c6bfb6e5ea",
    #             "4f9b279e336ec7d2639b0e1d3e8e5173be919c64c566e18d486a7dab1ab23312",
    #             "19089b4697ba35410d7149cc5b1d1d174da4f9fccd7b587928fd5cfb6f9551da",
    #             "d5a76f374dde0c1918d89e6f62be7252bad9b07b47bf02d2ff0769a0166df0a0",
    #             "856e410d8ce685cf61773654371f83e2ff91f8a7c1a1374a957cd47c75a8b717",
    #             "e23ad2db84433712f7d94a64b00338732dcd3385f7e8647e3b66bd8555c84393"
    #           ],
    #           "mounts": [
    #             {
    #               "destination": "/etc/resolv.conf",
    #               "options": [
    #                 "rbind",
    #                 "rshared",
    #                 "rw"
    #               ],
    #               "source": "sandbox:///tmp/atlas/resolvconf/.+",
    #               "type": "bind"
    #             }
    #           ],
    #           "name": "ccr-init",
    #           "no_new_privileges": false,
    #           "seccomp_profile_sha256": "",
    #           "signals": [],
    #           "user": {
    #             "group_idnames": [
    #               {
    #                 "pattern": "",
    #                 "strategy": "any"
    #               }
    #             ],
    #             "umask": "0022",
    #             "user_idname": {
    #               "pattern": "",
    #               "strategy": "any"
    #             }
    #           },
    #           "working_dir": "/root"
    #         },
    #         "i": 0
    #       }
    #     }
    #   ]
    # }
    $opaImage = "openpolicyagent/opa:0.69.0"
    if ($env:GITHUB_ACTIONS -eq "true") {
        $opaImage = "cleanroombuild.azurecr.io/openpolicyagent/opa:0.69.0"
    }

    $result = (docker run `
            -v ${outDir}/${containerName}-ccepolicy-input.rego:/input.rego `
            --rm `
            $opaImage eval `
            "some i; data.policy.containers[i].id == `"$containerImage`";container := data.policy.containers[i]" -d /input.rego)
    $containerPolicy = $result | jq  -r '.result[0].bindings.container' | ConvertFrom-Json

    if ($null -eq $containerPolicy) {
        throw "Did not get the policy for $containerImage"
    }

    return $containerPolicy
}