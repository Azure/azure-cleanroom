[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$outDir
)
# Sample repo: https://github.com/neelabalan/mongodb-sample-dataset
Write-Host "Downloading sample data to $outDir."
wget https://raw.githubusercontent.com/neelabalan/mongodb-sample-dataset/refs/heads/main/sample_supplies/sales.json -P $outDir