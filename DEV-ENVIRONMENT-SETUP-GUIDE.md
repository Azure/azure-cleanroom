
 # DevBox setup guide and local deployment of nginx-hello

This guide walks you through setting up a complete development environment using **WSL 2.4.x**, **Ubuntu 24.04**, **PowerShell**, Kubernetes (`kind`, `k9s`), Azure CLI, Helm, and more. By following this guide, you not only install all dependencies but also validate your environment by confirming that `nginx-hello` runs successfully as a pod `(virtual-cleanroom)` inside the cluster. 


##  1. Prepare WSL

> NOTE:
> Ensure you are using **WSL 2.4.x** only.

### Check Current Version

```powershell
wsl --version
```
If version is not 2.4.x, uninstall and downgrade:

###  Downgrade to 2.4.13

```powershell
wsl --uninstall
```

Install [WSL 2.4.13.0](https://github.com/microsoft/WSL/releases/download/2.4.13/wsl.2.4.13.0.x64.msi)

---
##  2. Install Ubuntu 24.04

```powershell
wsl.exe --install Ubuntu-24.04
```
---
## 3. Install Docker Desktop

1. Download Docker Desktop from [Docker Website](https://www.docker.com/products/docker-desktop/)
2. Enable WSL 2 backend during setup.
3. Restart WSL after install:

```powershell
wsl --shutdown
```
---
##  4. Install PowerShell on Ubuntu

### Prerequisites

```bash
sudo apt-get update
sudo apt-get install -y wget apt-transport-https software-properties-common
source /etc/os-release
```

### Add Microsoft Repo and Install PowerShell

```bash
wget -q https://packages.microsoft.com/config/ubuntu/$VERSION_ID/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y powershell
```

### Launch PowerShell

```bash
pwsh
```
---

## 5. Install kind (Kubernetes in Docker)

For **x86_64** systems:

```bash
[ $(uname -m) = x86_64 ] && curl -Lo ./kind https://kind.sigs.k8s.io/dl/v0.29.0/kind-linux-amd64
```

Make `kind` executable and move to bin:

```bash
chmod +x ./kind
sudo mv ./kind /usr/local/bin/kind
```

Create a cluster:

```bash
kind create cluster
```
---
## 6. Install K9s (Kubernetes TUI)

```bash
wget https://github.com/derailed/k9s/releases/download/v0.50.6/k9s_linux_amd64.deb
sudo dpkg -i ./k9s_linux_amd64.deb
```
---
## 7. Prepare Git Enlistment
Install GCM on Windows: 

[Download Git Credential Manager for Windows](https://github.com/git-ecosystem/git-credential-manager/blob/main/docs/install.md)

Configure Git Credential Manager:

```bash
git config --global credential.helper "/mnt/c/Program Files/Git/mingw64/bin/git-credential-manager.exe"
```

Clone your repo:

```bash
git clone https://github.com/azure-core/azure-cleanroom
```
---
## 8. Set Up Dev Environment Tools

### Install Azure CLI (local, no Windows integration)

```bash
rm -fr ~/.azure
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
```

### Install ORAS

```bash
pushd /tmp
curl -LO "https://github.com/oras-project/oras/releases/download/v1.2.0/oras_1.2.0_linux_amd64.tar.gz"
mkdir -p oras-install/
tar -zxf oras_1.2.0_*.tar.gz -C oras-install/
sudo mv oras-install/oras /usr/local/bin/
rm -rf ./oras-install/
rm ./oras_*.gz
popd
```

### Install PowerShell Module: `powershell-yaml`

```powershell
Install-Module powershell-yaml
```

### Install `jq`

```bash
sudo apt-get install jq
```

### Install Helm

```bash
curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
```

### Install AzCopy

```bash
wget -O azcopy_v10.tar.gz https://aka.ms/downloadazcopy-v10-linux
tar -xf azcopy_v10.tar.gz --strip-components=1
sudo mv azcopy /usr/bin
rm azcopy_v10.tar.gz
```
### Install Python and Go

```bash
# Python and venv
sudo apt-get update
sudo apt-get install -y python3 python3-venv python3-pip

# Golang
sudo apt-get install -y golang
```
### Recommended VS Code Extensions

- ms-dotnettools.csharp
- ms-python.python
- ms-python.black-formatter
- ms-python.isort
- golang.go
- redhat.vscode-yaml
- ms-azuretools.vscode-docker
- ms-kubernetes-tools.vscode-kubernetes-tools
- tsandall.opa
---
## 9. Deploy `nginx-hello` Locally (Build → Kind Up → Run-Collab)

### Step 1: Navigate to Project Root

```powershell
cd ~/azure-cleanroom
```

---

### Step 2: Build Cleanroom Containers

```powershell
./build/onebox/build-local-cleanroom-containers.ps1
```

---

### Step 3: Bring Up Kind Cluster

```powershell
./test/onebox/kind-up.sh
```

---

### Step 4: Launch `virtual-cleanroom` Pod

```powershell
./test/onebox/multi-party-collab/nginx-hello/run-collab.ps1
```

This deploys the app into the kind cluster, creating a pod named `virtual-cleanroom`.

---

### Step 5: Inspect via `k9s`

```bash
k9s
```

Look for the `virtual-cleanroom` pod:

- `/` to search
- `l` to view logs
- `d` to describe pod
- `q` / `Ctrl+C` to exit

You can now extend this environment to deploy real apps, run multi-party workloads, and debug interactions all from your local DevBox.


---
