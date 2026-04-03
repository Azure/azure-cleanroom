
 # DevBox setup guide and local deployment of nginx-hello <!-- omit from toc -->

- [1. Install Ubuntu 24.04](#1-install-ubuntu-2404)
- [2. Install Docker Desktop](#2-install-docker-desktop)
- [3. Install PowerShell on Ubuntu](#3-install-powershell-on-ubuntu)
  - [3.1. Prerequisites](#31-prerequisites)
  - [3.2. Add Microsoft Repo and Install PowerShell](#32-add-microsoft-repo-and-install-powershell)
  - [3.3. Launch PowerShell](#33-launch-powershell)
- [4. Install kind (Kubernetes in Docker)](#4-install-kind-kubernetes-in-docker)
- [5. Install K9s (Kubernetes TUI)](#5-install-k9s-kubernetes-tui)
- [6. Prepare Git Enlistment](#6-prepare-git-enlistment)
- [7. Set Up Dev Environment Tools](#7-set-up-dev-environment-tools)
  - [7.1. Install Azure CLI (local, no Windows integration)](#71-install-azure-cli-local-no-windows-integration)
  - [7.2. Install ORAS](#72-install-oras)
  - [7.3. Install PowerShell Module: `powershell-yaml`](#73-install-powershell-module-powershell-yaml)
  - [7.4. Install `jq`](#74-install-jq)
  - [7.5. Install Helm](#75-install-helm)
  - [7.6. Install AzCopy](#76-install-azcopy)
  - [7.7. Install Go](#77-install-go)
  - [7.8. Install python and uv for dependency management](#78-install-python-and-uv-for-dependency-management)
    - [7.8.1. Integrate uv with VSCode for development](#781-integrate-uv-with-vscode-for-development)
    - [7.8.2. How to create a new python project (using uv)](#782-how-to-create-a-new-python-project-using-uv)
  - [7.9. Install sbt and Scala](#79-install-sbt-and-scala)
  - [7.10. Recommended VS Code Extensions](#710-recommended-vs-code-extensions)
- [8. Deploy `nginx-hello` Locally (Build → Kind Up → Run-Collab)](#8-deploy-nginx-hello-locally-build--kind-up--run-collab)
  - [8.1. Step 1: Navigate to Project Root](#81-step-1-navigate-to-project-root)
  - [8.2. Step 2: Build Cleanroom Containers](#82-step-2-build-cleanroom-containers)
  - [8.3. Step 3: Bring Up Kind Cluster](#83-step-3-bring-up-kind-cluster)
  - [8.4. Step 4: Launch `virtual-cleanroom` Pod](#84-step-4-launch-virtual-cleanroom-pod)
  - [8.5. Step 5: Inspect via `k9s`](#85-step-5-inspect-via-k9s)
- [9. Deploy 1P workloads Locally](#9-deploy-1p-workloads-locally)

This guide walks you through setting up a complete development environment using **WSL 2.4.x**, **Ubuntu 24.04**, **PowerShell**, Kubernetes (`kind`, `k9s`), Azure CLI, Helm, and more. By following this guide, you not only install all dependencies but also validate your environment by confirming that `nginx-hello` runs successfully as a pod `(virtual-cleanroom)` inside the cluster. 

This setup turns a Windows machine into a local Linux-based Kubernetes development environment.
At a high level, the stack looks like this (top to bottom):

```
Your laptop (Windows)
└── WSL 2 (lightweight Linux VM)
    └── Ubuntu 24.04 (Linux userland)
        └── Docker Engine
            └── KinD (Kubernetes-in-Docker)
                └── Kubernetes cluster
                    └── Pod: virtual-cleanroom
                        └── Container: nginx-hello (user application)
```

---
##  1. Install Ubuntu 24.04

```powershell
wsl.exe --install Ubuntu-24.04
```
---
## 2. Install Docker Desktop

1. Download Docker Desktop from [Docker Website](https://www.docker.com/products/docker-desktop/)
2. Enable WSL 2 backend during setup.
3. Restart WSL after install:

```powershell
wsl --shutdown
```
---
##  3. Install PowerShell on Ubuntu

### 3.1. Prerequisites

```bash
sudo apt-get update
sudo apt-get install -y wget apt-transport-https software-properties-common
source /etc/os-release
```

### 3.2. Add Microsoft Repo and Install PowerShell

```bash
wget -q https://packages.microsoft.com/config/ubuntu/$VERSION_ID/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y powershell
```

### 3.3. Launch PowerShell

```bash
pwsh
```
---

## 4. Install kind (Kubernetes in Docker)

For **x86_64** systems:

```bash
curl -Lo ./kind https://kind.sigs.k8s.io/dl/v0.30.0/kind-linux-amd64
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
## 5. Install K9s (Kubernetes TUI)

```bash
wget https://github.com/derailed/k9s/releases/latest/download/k9s_linux_amd64.deb
sudo dpkg -i ./k9s_linux_amd64.deb
rm ./k9s_linux_amd64.deb
```
---
## 6. Prepare Git Enlistment
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
## 7. Set Up Dev Environment Tools

### 7.1. Install Azure CLI (local, no Windows integration)

```bash
rm -fr ~/.azure
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
```

### 7.2. Install ORAS

```bash
pushd /tmp
curl -LO "https://github.com/oras-project/oras/releases/download/v1.3.0/oras_1.3.0_linux_amd64.tar.gz"
mkdir -p oras-install/
tar -zxf oras_1.3.0_*.tar.gz -C oras-install/
sudo mv oras-install/oras /usr/local/bin/
rm -rf ./oras-install/
rm ./oras_*.gz
popd
```

### 7.3. Install PowerShell Module: `powershell-yaml`

```powershell
Install-Module powershell-yaml
```

### 7.4. Install `jq`

```bash
sudo apt-get install jq
```

### 7.5. Install Helm

```bash
curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
```

### 7.6. Install AzCopy

```bash
wget -O azcopy_v10.tar.gz https://aka.ms/downloadazcopy-v10-linux
tar -xf azcopy_v10.tar.gz --strip-components=1
sudo mv azcopy /usr/bin
rm azcopy_v10.tar.gz
```
### 7.7. Install Go

```bash
# Python and venv
sudo apt-get update
# Golang
sudo apt-get install -y golang
```

### 7.8. Install python and uv for dependency management

```bash
# Install uv (Python package and project manager)
curl -LsSf https://astral.sh/uv/install.sh | sh

# Reload shell or source the profile to update PATH
source ~/.bashrc
# or restart your terminal

# Verify installation
uv --version

# Install python
uv python install 3.12 3.10
```

> **Note:** `uv` is a fast Python package installer and resolver, written in Rust. It's used for managing Python dependencies in this project instead of traditional `pip` workflows. It provides significantly faster dependency resolution and installation.
>
> **In azure-cleanroom:** The repository uses uv with a workspace configuration to manage multiple Python packages (`cleanroom-internal`, `cleanroom-sdk`, `blobfuse-launcher`, `s3fs-launcher`, etc.). All dependencies are centralized in the root `pyproject.toml` with workspace members defined for each sub-project. This ensures consistent dependency versions across all components and enables efficient cross-package development.

#### 7.8.1. Integrate uv with VSCode for development
For VSCode to pickup local package dependencies within the workspace we need to create a venv using uv, activate it and set it as the interpreter for the python projects

```pwsh
cd ~/azure-cleanroom
uv venv
./.venv/bin/activate.ps1

# Install all dependencies.
uv lock
uv sync --all-packages
```
In VS code, navigate to a python file and set the interpreter to the path ./venv/bin/python. Reload VS Code. All the dependencies should be resolved now.

#### 7.8.2. How to create a new python project (using uv)

```bash
# Navigate to the workspace root
cd ~/azure-cleanroom/src
uv init my-new-service [ --package | --lib | --app ]
```

**Add to workspace:**

```bash
cd ~/azure-cleanroom
uv workspace add "src/my-new-service"
```

**Add dependencies to project**
```bash
cd ~/azure-cleanroom/src/my-new-service
uv add requests==2.32.4
```

**Install workspace dependencies:**

```bash
cd ~/azure-cleanroom
uv sync --all-packages
```

**Build all packages**
```bash
cd ~/azure-cleanroom
uv build --wheel --all-packages
```

### 7.9. Install sbt and Scala

**Install JDK (prerequisite for sbt)**

```bash
# Install OpenJDK 17 or later
sudo apt-get update
sudo apt-get install openjdk-17-jdk -y

# Verify installation
java -version
```

**Install sbt (Scala Build Tool)**

```bash
# For Ubuntu/Debian
sudo apt-get update
sudo apt-get install apt-transport-https curl gnupg -yqq
echo "deb https://repo.scala-sbt.org/scalasbt/debian all main" | sudo tee /etc/apt/sources.list.d/sbt.list
echo "deb https://repo.scala-sbt.org/scalasbt/debian /" | sudo tee /etc/apt/sources.list.d/sbt_old.list
curl -sL "https://keyserver.ubuntu.com/pks/lookup?op=get&search=0x2EE0EA64E40A89B84B2DF73499E82A75642AC823" | sudo -H gpg --no-default-keyring --keyring gnupg-ring:/etc/apt/trusted.gpg.d/scalasbt-release.gpg --import
sudo chmod 644 /etc/apt/trusted.gpg.d/scalasbt-release.gpg
sudo apt-get update
sudo apt-get install sbt
```

**Verify installation**
```bash
sbt --version
```

**Install Scala**
```bash
sudo apt-get install scala
scala -version
```

### 7.10. Recommended VS Code Extensions

- ms-dotnettools.csharp
- ms-python.python
- ms-python.black-formatter
- ms-python.isort
- golang.go
- redhat.vscode-yaml
- ms-azuretools.vscode-docker
- ms-kubernetes-tools.vscode-kubernetes-tools
- tsandall.opa
- scalameta.metals (Scala language server)
- scala-lang.scala (Scala syntax highlighting)

---
## 8. Deploy `nginx-hello` Locally (Build → Kind Up → Run-Collab)

### 8.1. Step 1: Navigate to Project Root

```powershell
cd ~/azure-cleanroom
```

---

### 8.2. Step 2: Build Cleanroom Containers

```powershell
./build/onebox/build-containers.ps1
```

---

### 8.3. Step 3: Bring Up Kind Cluster

```powershell
./test/onebox/kind-up.sh
```

---

### 8.4. Step 4: Launch `virtual-cleanroom` Pod

```powershell
./test/onebox/multi-party-collab/nginx-hello/run-collab.ps1
```

This deploys the app into the kind cluster, creating a pod named `virtual-cleanroom`.

---

### 8.5. Step 5: Inspect via `k9s`

```bash
k9s
```

Look for the `virtual-cleanroom` pod:

- `/` to search
- `l` to view logs
- `d` to describe pod
- `q` / `Ctrl+C` to exit

You can now extend this environment to deploy real apps, run multi-party workloads, and debug interactions all from your local DevBox.

## 9. Deploy 1P workloads Locally

See steps [here](./test/onebox/README-1P.md) to run scenarios such as big data query analytics locally.

### Quick local run commands <!-- omit from toc -->

```pwsh
# Not needed if already built the containers
./build/onebox/build-containers.ps1

# Setup environment and kind cluster
./test/onebox/workloads/setup-env.ps1

# Run big-data-query-analytics scenario
./test/onebox/multi-party-collab/big-data-query-analytics/test-big-data-analytics.ps1
```