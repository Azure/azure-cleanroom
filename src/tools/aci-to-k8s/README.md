# ACI ARM Template to K8s spec converter

The tool takes as input the ARM template generated by the `az cleanroom` CLI and converts it to a K8s deployment spec so that for local development/testing the clean room can be deployed to a local `kind` cluster.

## Try it out
Build the tool:
```powershell
$root = git rev-parse --show-toplevel
pwsh $root/build/onebox/build-aci-to-k8s.ps1
```
Run it:
```powershell
docker run --rm -v $root/src/tools/aci-to-k8s/samples:/workspace -w /workspace -u $(id -u $env:USER) aci-to-k8s --template-file sample-template.json --out-dir .
```
Above command will generate `virtual-cleanroom-deployment.yaml` file in the `samples` folder.

## Help
To see the command line options run:
```powershell
docker run aci-to-k8s --help
```
