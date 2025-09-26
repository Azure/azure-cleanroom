param (
    [CmdletBinding()]
    [Parameter(Mandatory = $true)]
    [string]$tag
)

# Check if the tag is in the format of x.y.z (with optional parts)
if ($tag -match '^\d+\.\d+\.\d+') {
    $parts = $tag.Split('.')
    $major = $parts[0]
    $minor = $parts[1]
    $patch = $parts[2]
    $extra = ""
    if ($parts.Count -eq 4) {
        $extra = "-" + $parts[3]
    }

    return "$major.$minor.$patch$extra"
}

# If not a version format, truncate to 8 characters and use as suffix
$truncatedTag = $tag
if ($tag.Length -gt 8) {
    $truncatedTag = $tag.Substring(0, 8)
}

# Return the formatted version. Add a "v" prefix below as $truncatedTag like 0207 gives
# error "Version segment starts with 0".
return "1.0.42-v$truncatedTag"
