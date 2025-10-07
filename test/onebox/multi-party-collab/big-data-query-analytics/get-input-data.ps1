
function Get-InputData {
    param(
        [string]
        $dataDir,

        $handles,

        [DateTimeOffset]
        $startDate
    )

    #https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
    $ErrorActionPreference = 'Stop'
    $PSNativeCommandUseErrorActionPreference = $true

    # Use sample dataset at https://github.com/Azure-Samples/Synapse/tree/main/Data/Tweets
    $src = "https://github.com/Azure-Samples/Synapse/raw/refs/heads/main/Data/Tweets"

    Write-Output "Downloading data from '$src'..."

    foreach ($handle in $handles) {
        $destDir = $dataDir
        if ($startDate -ne [DateTimeOffset]::MinValue) {
            $destDir = Join-Path -Path $dataDir -ChildPath $startDate.ToString("yyyy-MM-dd")
            mkdir -p $destDir
            $startDate = $startDate.AddDays(1)
        }

        Write-Output "Downloading data for '$handle' to {$destDir}..."
        curl -sS -L "$src/$handle.csv" -o "$destDir/$handle.csv"
    }

    Write-Output "Downloaded data '$dataDir'."
}

function Get-PublisherData {
    param(
        [string]
        $dataDir,

        [DateTimeOffset]
        $startDate = [DateTimeOffset]::MinValue
    )
    $handles = ("RahulPotharajuTweets", "raghurwiTweets", "MikeDoesBigDataTweets", "SQLCindyTweets")
    Get-InputData -dataDir $dataDir -handles $handles -startDate $startDate
}

function Get-ConsumerData {
    param(
        [string]
        $dataDir,

        [DateTimeOffset]
        $startDate = [DateTimeOffset]::MinValue
    )
    $handles = ("BrigitMurtaughTweets", "FranmerMSTweets", "JeremyLiknessTweets", "mwinkleTweets")
    Get-InputData -dataDir $dataDir -handles $handles -startDate $startDate
}