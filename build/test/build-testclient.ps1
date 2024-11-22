param(
  [parameter(Mandatory=$false)]
  [string]$tag = "latest",

  [parameter(Mandatory=$false)]
  [string]$repo = "docker.io",

  [parameter(Mandatory=$false)]
  [switch]$push
)

. $PSScriptRoot/../helpers.ps1

if ($repo)
{
  $imageName = "$repo/testclient:$tag"
}
else
{
  $imageName = "testclient:$tag"
}

$root = git rev-parse --show-toplevel
docker image build -t $imageName `
  -f $PSScriptRoot/../docker/Dockerfile.testclient $root
CheckLastExitCode

if ($push)
{
    docker push $imageName
    CheckLastExitCode
}
