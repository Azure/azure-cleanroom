$uid = id -u ${env:USER}
$gid = id -g ${env:USER}

mkdir -p $PSScriptRoot/models/python
docker run `
    -u ${uid}:${gid} `
    -v $PSScriptRoot/:/input `
    -v $PSScriptRoot/models/python:/output `
    --name datamodel-codegen `
    --rm koxudaxi/datamodel-code-generator `
    --input /input/cleanroomspec-openapi.yaml --output /output/model.py


$header = @"
# DO NOT EDIT. AUTO-GENERATED CODE.
# Please run tools/generate_models.ps1 to re-generate the file after editing cleanroomspec-openapi.yaml.
"@

$model = Get-Content -Path $PSScriptRoot/models/python/model.py
Set-Content $PSScriptRoot/models/python/model.py -Value $header, $([environment]::NewLine), $model

docker run `
    -v $PSScriptRoot/models/python:/src `
    --workdir /src `
    --name black-formatter `
    --rm pyfound/black:latest_release black . -t py311