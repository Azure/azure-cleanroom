function Test-DatasetCount {
    param(
        [Parameter(Mandatory = $true)]
        [string]$frontendEndpoint,

        [Parameter(Mandatory = $true)]
        [string]$collaborationName,

        [Parameter(Mandatory = $true)]
        [string]$userToken,

        [Parameter(Mandatory = $true)]
        [string]$runId,

        [Parameter(Mandatory = $true)]
        [int]$expectedCount
    )

    Write-Output "Listing all datasets..."
    $datasetsJson = curl --fail-with-body -sS -X GET `
        "http://${frontendEndpoint}/collaborations/${collaborationName}/analytics/datasets?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $datasetsList = $datasetsJson | ConvertFrom-Json
    $currentRunDatasets = $datasetsList.value | Where-Object { $_.id -like "*-$runId" }
    $actualCount = $currentRunDatasets.Count

    if ($actualCount -eq $expectedCount) {
        Write-Host "Successfully verified $actualCount datasets published (Expected: $expectedCount)" `
            -ForegroundColor Green
    }
    else {
        Write-Host "Dataset count mismatch. Expected: $expectedCount, Actual: $actualCount" `
            -ForegroundColor Red
        throw "Expected $expectedCount datasets but found $actualCount"
    }
}
function Test-QueryCount {
    param(
        [Parameter(Mandatory = $true)]
        [string]$frontendEndpoint,

        [Parameter(Mandatory = $true)]
        [string]$collaborationName,

        [Parameter(Mandatory = $true)]
        [string]$userToken,

        [Parameter(Mandatory = $true)]
        [string]$runId,

        [Parameter(Mandatory = $true)]
        [int]$expectedCount
    )

    Write-Output "Listing all queries via frontend API..."
    $queriesJson = curl --fail-with-body -sS -X GET `
        "http://${frontendEndpoint}/collaborations/${collaborationName}/analytics/queries?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $queries = $queriesJson | ConvertFrom-Json
    $currentRunQueries = $queries.value | Where-Object { $_.id -like "*-$runId" }
    $actualCount = $currentRunQueries.Count

    if ($actualCount -eq $expectedCount) {
        Write-Host "Successfully verified $actualCount queries published (Expected: $expectedCount)" `
            -ForegroundColor Green
    }
    else {
        Write-Host "Query count mismatch. Expected: $expectedCount, Actual: $actualCount" `
            -ForegroundColor Red
        throw "Expected $expectedCount queries but found $actualCount"
    }
}
function Test-QueriesForDataset {
    param(
        [Parameter(Mandatory = $true)]
        [string]$frontendEndpoint,

        [Parameter(Mandatory = $true)]
        [string]$collaborationName,

        [Parameter(Mandatory = $true)]
        [string]$userToken,

        [Parameter(Mandatory = $true)]
        [string]$datasetId,

        [Parameter(Mandatory = $true)]
        [int]$expectedQueryCount,

        [Parameter(Mandatory = $false)]
        [string[]]$expectedQueryIds = @()
    )

    Write-Output "Queries using dataset: $datasetId"
    $queryByDatasetJson = curl --fail-with-body -sS -X GET `
        "http://${frontendEndpoint}/collaborations/${collaborationName}/analytics/datasets/${datasetId}/queries?api-version=2026-03-01-preview" `
        -H "content-type: application/json" `
        -H "Authorization: Bearer $userToken"

    $queryByDataset = $queryByDatasetJson | ConvertFrom-Json
    $queryCount = $queryByDataset.value.Count

    if ($queryCount -ne $expectedQueryCount) {
        Write-Host "Query count mismatch for dataset $datasetId. Expected: $expectedQueryCount, Actual: $queryCount" `
            -ForegroundColor Red
        throw "Expected $expectedQueryCount queries but found $queryCount for dataset $datasetId"
    }

    if ($expectedQueryIds.Count -gt 0) {
        $actualQueryIds = $queryByDataset.value | ForEach-Object { $_.id }
        foreach ($expectedId in $expectedQueryIds) {
            if ($actualQueryIds -notcontains $expectedId) {
                Write-Host "Expected query ID '$expectedId' not found in queries for dataset $datasetId" `
                    -ForegroundColor Red
                throw "Expected query ID '$expectedId' not found in queries for dataset $datasetId"
            }
        }
        Write-Host "Successfully verified $queryCount queries using dataset $datasetId (Expected: $expectedQueryCount)" `
            -ForegroundColor Green
        Write-Host "All expected query IDs verified for dataset $datasetId" `
            -ForegroundColor Green
    }
    else {
        Write-Host "Successfully verified $queryCount queries using dataset $datasetId (Expected: $expectedQueryCount)" `
            -ForegroundColor Green
    }
}
