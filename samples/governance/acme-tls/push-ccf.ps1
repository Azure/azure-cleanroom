param(
    [switch]
    $acme,
    [string]
    $acrLoginServer = "docker.io/gausinha"
)

if ($acme) {
    $tag = "latest"
    docker tag "ccf-acme:$tag" "$acrLoginServer/ccf-acme:$tag"
    docker push "$acrLoginServer/ccf-acme:$tag"
}

