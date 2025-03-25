[CmdletBinding()]
param
(
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

# For local runs build and launch the test in a container.
if ($env:GITHUB_ACTIONS -ne "true") {
    # https://github.com/dotnet/dotnet-docker/blob/main/samples/run-tests-in-sdk-container.md
    docker run --rm --network host -v ${root}:/app -w /app/src/governance/test mcr.microsoft.com/dotnet/sdk:8.0 dotnet test --logger "console;verbosity=normal"
}
else {
    dotnet test $root/src/governance/test/cgs-tests.csproj --logger "trx;LogFileName=TestRunResult-CGS.trx" --logger "console;verbosity=normal"
}

# Run this after CGS tests as below adds a member and leaves it in Accepted state. This interferes
# with our tests as contract proposal acceptance requires no members that are yet to become active.
pwsh $root/src/governance/test/test-ccf-operator-actions.ps1 -WithWorkaround
