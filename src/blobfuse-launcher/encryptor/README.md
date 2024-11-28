# AES Encryptor invocation sample from Python
A go module that exposes `EncryptChunk` and `DecryptChunk` method to showcase how to invoke the same from Python via the `az cleanroom` cli. To try this sample follow the below steps:
```powershell
$root=$(git rev-parse --show-toplevel)
cd $root/src/tools/aes_encryptor

# Build aes_encryptor.so
go build -o aes_encryptor.so -buildmode=c-shared aes_encryptor.go

# Drop aes_encryptor.so into the binaries folder and build to pack it in with the cli extension.
mv ./aes_encryptor.so $root/src/tools/azure-cli-extension/cleanroom/azext_cleanroom/binaries
../../../build/build-azcliext-cleanroom.ps1

```

## Links
- https://medium.com/analytics-vidhya/running-go-code-from-python-a65b3ae34a2d
- https://fluhus.github.io/snopher/


# Running Encryptor Plugin with Azure-Storage-Fuse
The Encryptor module is responsible for enabling client-side encryption with Blobfuse. This plugin will be loaded as a component in Blobfuse via the custom component feature. To generate the encryptor .so, follow the steps below:
```powershell
$root=$(git rev-parse --show-toplevel)
cd $root/src/blobfuse-launcher

# Move the encryptor plugin to Azure-Storage-Fuse
mv ./encryptor $root/external/azure-storage-fuse/

cd $root/external/azure-storage-fuse

# Build the Encryptor plugin
go build -o encryptor.so -buildmode=plugin encryptor.go

```