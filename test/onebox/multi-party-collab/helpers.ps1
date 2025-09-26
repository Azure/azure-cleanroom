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