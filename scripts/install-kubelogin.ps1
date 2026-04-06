#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

sudo apt-get update -y
sudo apt-get install -y unzip

curl -Lo /tmp/kubelogin-linux-amd64.zip `
    https://github.com/Azure/kubelogin/releases/download/v0.2.14/kubelogin-linux-amd64.zip

unzip -o /tmp/kubelogin-linux-amd64.zip -d /tmp

sudo mv /tmp/bin/linux_amd64/kubelogin /usr/local/bin/kubelogin
sudo chmod +x /usr/local/bin/kubelogin

kubelogin --version
