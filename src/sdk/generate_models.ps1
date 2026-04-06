$uid = id -u ${env:USER}
$gid = id -g ${env:USER}

# Python model generation
mkdir -p $PSScriptRoot/models/python
docker run `
    -u ${uid}:${gid} `
    -v $PSScriptRoot/openapi:/input `
    -v $PSScriptRoot/src/cleanroom_sdk/models:/output `
    --name datamodel-codegen `
    --rm koxudaxi/datamodel-code-generator `
    --input /input `
    --output /output `
    --input-file-type openapi `
    --disable-timestamp

$files = Get-ChildItem -Path $PSScriptRoot/src/cleanroom_sdk/models -Name -File | Where-Object { $_ -ne "__init__.py" } 

foreach ($file in $files) {
    $fileName = $file.Split(".py")[0]
    $header = @"
# DO NOT EDIT. AUTO-GENERATED CODE.
# Please run tools/generate_models.ps1 to re-generate the file after editing openapi/$fileName.yaml.
"@

    $model = Get-Content -Path $PSScriptRoot/src/cleanroom_sdk/models/$file
    Set-Content $PSScriptRoot/src/cleanroom_sdk/models/$file -Value $header, $([environment]::NewLine), $model
}

docker run `
    -v $PSScriptRoot/src/cleanroom_sdk/models:/src `
    --workdir /src `
    --name black-formatter `
    --rm pyfound/black:latest_release black . -t py311
