<#
.SYNOPSIS
    Run CGS tests. Run deploy-cgs.ps1 before running this script.
.DESCRIPTION
    Run CGS tests.
    If running locally (not in GitHub Actions), the tests are run in a .NET SDK container.
    If running in GitHub Actions, the tests are run directly.
.PARAMETER TestFilter
    Test filter. E.g., Test.UserDocumentTests, Test.UserDocumentTests.ListUserDocumentsWithQueryParameter
.EXAMPLE
    ./test-cgs.ps1
    Run all tests.
.EXAMPLE
    ./test-cgs.ps1 -TestFilter Test.UserDocumentTests
    Run all tests in the Test.UserDocumentTests class.
.EXAMPLE
    ./test-cgs.ps1 -TestFilter Test.UserDocumentTests.ListUserDocumentsWithQueryParameter
    Run the Test.UserDocumentTests.ListUserDocumentsWithQueryParameter test.
#>
[CmdletBinding()]
param
(
    [string]$TestFilter = ""
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

# For local runs build and launch the test in a container.
if ($env:GITHUB_ACTIONS -ne "true") {
    # https://github.com/dotnet/dotnet-docker/blob/main/samples/run-tests-in-sdk-container.md

    if ($TestFilter -ne "") {
        docker run --rm --network host -v ${root}:/app -w /app/src/governance/test mcr.microsoft.com/dotnet/sdk:10.0 dotnet test --logger "console;verbosity=normal" --filter $TestFilter
    }
    else {
        docker run --rm --network host -v ${root}:/app -w /app/src/governance/test mcr.microsoft.com/dotnet/sdk:10.0 dotnet test --logger "console;verbosity=normal"
    }
}
else {
    dotnet test $root/src/governance/test/cgs-tests.csproj --logger "trx;LogFileName=TestRunResult-CGS.trx" --logger "console;verbosity=normal"
}