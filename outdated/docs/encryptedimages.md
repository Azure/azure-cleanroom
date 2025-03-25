## Encrypted container images
Creating an RSA key in Azure Key Vault MHSM for SKR and use the same to create encrypted container images.
See [here](skr.md) for instructions to create an RSA key in AKV MHSM. The public key would be then used to encrypt container images via `podman`:
```sh
# Download the public key pem file from AKV created per the instruction link above.
$hsmName="cleanroom-mshm-test" # Change as per your setup
$keyName="imagekey" # Change as per your setup
az keyvault key download --hsm-name $hsmName --name $keyName --encoding PEM --file publickey.pem

# For Ubuntu: Launch a podman container so that we can use latest podman v4 on Ubuntu. Run all subsequent commands within this container as --encryption-key option is available in v4.4 onwards.
docker run -it -v $PWD:/work --privileged quay.io/podman/stable /bin/bash

# Start local registry to host enc. container image
podman run -d -p 5000:5000 --restart=always --name registry registry:2.7.1

# push encrypted alpine image to local registry
podman pull alpine:latest
podman tag alpine:latest localhost:5000/alpine:jwe

# <pemfile> is the public key downloaded from AKV per the instruction link above.
podman push --tls-verify=false --encryption-key jwe:/work/publickey.pem localhost:5000/alpine:jwe

# Remove locally cached (unencrypted) image that has now been pushed with encryption.
podman image rm localhost:5000/alpine:jwe

# Try pulling and running the image without specifying any decryption key
 podman pull --tls-verify=false localhost:5000/alpine:jwe
Error: writing blob: adding layer with blob "sha256:726b76e09c98b70dd1c4363d3157e68ee837911660b3ecef12b3724118c9272e": processing tar file(archive/tar: invalid tar header): exit status 1
```

