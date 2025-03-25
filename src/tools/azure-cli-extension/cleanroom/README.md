# Clean Room Azure CLI extension<!-- omit in toc -->

- [Prerequisites](#prerequisites)
- [Build and install](#build-and-install)
  - [Build and install using Docker](#build-and-install-using-docker)
  - [Build and install using a local python environment](#build-and-install-using-a-local-python-environment)
  - [Removing extension installation](#removing-extension-installation)
- [Usage](#usage)
  - [Governance](#governance)
    - [Client management](#client-management)
    - [Member activation](#member-activation)
    - [Service management](#service-management)
    - [Contract management](#contract-management)
    - [Deployment template management](#deployment-template-management)
    - [Clean room policy management](#clean-room-policy-management)
    - [Secrets management](#secrets-management)
    - [OIDC Issuer (IdP) management](#oidc-issuer-idp-management)
    - [Certificate Authority (CA) management](#certificate-authority-ca-management)
    - [Controlling execution](#controlling-execution)
    - [Controlling Log collection](#controlling-log-collection)
    - [Controlling Telemetry collection](#controlling-telemetry-collection)
    - [Events management](#events-management)
    - [Document management](#document-management)
    - [Member addition](#member-addition)
      - [Using member keys stored in Azure Key Vault](#using-member-keys-stored-in-azure-key-vault)
  - [CCF](#ccf)
    - [Provider management](#provider-management)
    - [Network management](#network-management)
      - [Platform: AMD SEV-SNP using Confidential ACI](#platform-amd-sev-snp-using-confidential-aci)
      - [Platform: Insecure Virtual using Docker](#platform-insecure-virtual-using-docker)
      - [Platform: Insecure Virtual using ACI](#platform-insecure-virtual-using-aci)
- [Links](#links)
- [Generating code under `vendored_sdks`](#generating-code-under-vendored_sdks)

Open a `pwsh` command prompt and follow the below instructions.

# Prerequisites

**Azure CLI**: You must have Azure CLI installed on your local computer. If you need to install or upgrade, see [Install the Azure CLI][azure-cli-install].

**Docker**: You need Docker installed locally. Docker provides packages that configure the Docker environment on [macOS][docker-mac], [Windows][docker-windows], and [Linux][docker-linux].

<!-- LINKS - External -->
[docker-get-started]: https://docs.docker.com/engine/docker-overview/
[docker-linux]: https://docs.docker.com/engine/installation/#supported-platforms
[docker-mac]: https://docs.docker.com/docker-for-mac/
[docker-windows]: https://docs.docker.com/docker-for-windows/
[azure-cli-install]: https://learn.microsoft.com/en-us/cli/azure/install-azure-cli

# Build and install
## Build and install using Docker
**Generate and install `whl` file**
```powershell
$root/build/build-azcliext-cleanroom.ps1

# Verify that extension is available.
az cleanroom -h
```

## Build and install using a local python environment
*Steps below are work in progress as not able to install in dev mode but need to use whl based install*

**One time actions for local development**
```powershell
$root=$(git rev-parse --show-toplevel)
$envname=".azcli-env"
$extname="cleanroom"
python3 -m venv $root/$envname
. $root/$envname/bin/Activate.ps1
pip install -r $root/src/tools/azure-cli-extension/cleanroom/requirements.txt
azdev extension repo add $root
```

**Generate and install `whl` file**
```powershell
# Activate the venv, if not already.
$root=$(git rev-parse --show-toplevel)
$envname=".azcli-env"
$extname="cleanroom"
python3 -m venv $root/$envname
. $root/$envname/bin/Activate.ps1

# Build the whl file and install it locally.
$root/build/build-azcliext-cleanroom.ps1 -localenv

# Verify that extension is available.
az cleanroom -h
```

## Removing extension installation
```powershell
az extension remove --name cleanroom
```

# Usage
## Governance
**One-time actions**
  - Create the member certificate and private key for the first member by running the below command:
    ```powershell
    az cleanroom governance member keygenerator-sh | bash -s -- --name member0
    ```
    This will generate two files: `member0_cert.pem` and `member0_privk.pem`.

  - Create a CCF instance per steps [here](../../../ccf/README.md#3-quickstart-ccf-network-creation).

### Client management
All `governance` commands below need a client deployment. The `client deploy` command will launch the client-side containers on your local machine to interact with CCF instance. Specify a friendly name via `--name` to refer to this deployment instance.
```powershell
# Deploy client-side containers to interact with the governance service.
az cleanroom governance client deploy `
  --ccf-endpoint "https://<name>.<region>.azurecontainer.io" `
  --signing-cert ./member0_cert.pem `
  --signing-key ./member0_privk.pem `
  --service-cert ./service_cert.pem `
  --name "cl-governance"

# See information about the client configuration.
az cleanroom governance client show --name "cl-governance"
{
  "ccfEndpoint": "https://<name>.<region>.azurecontainer.io",
  "memberData": {
    "identifier": <...>
  },
  "memberId": "5512fdc11c2339200c761e15e06c0fdee30345e244de20a3d45f785303fb3308",
  "serviceCert": <...>,
  "signingCert": <...>,
  "signingKey": "<redacted>"
}

# See information about the container deployment.
az cleanroom governance client show-deployment --name "cl-governance"
{
  "ports": {
    "cgs-client": 32802,
    "cgs-ui": 32801
  },
  "projectName": "cl-governance",
  "uiLink": "http://localhost:32801"
}

```
All governance related commands also require `--governance-client` parameter with the value used above. You can choose to set the default client name via `az config` command to avoid repeatedly passing in the parameter.
```powershell
# Set it once or pass --governance-client with each command.
az config set cleanroom.governance.client_name="cl-governance"
```
### Member activation
A new member who gets registered in CCF is not yet able to participate in governance operations. To do so, the new member should first acknowledge that they are satisfied with the state of the service (for example, after auditing the current constitution and the nodes currently trusted). Once satisfied the new member should accept their membership by running the below command:
```powershell
az cleanroom governance member activate
```
Once the command completes, the new member becomes active and can take part in governance operations (e.g. creating a new proposal or voting for an existing one). You can verify the activation of the member by running the below command:
```powershell
az cleanroom governance member show
{
  "2f1972bcb4b2fcfcb1e08e09c96f6cf9ac6ae107bf3ea1b1b3b258fb64b0f585": {
    "cert": <...>,
    "member_data": <...>,
    "status": "Active"
  }
}
```

### Service management
Use the following command to to deploy the governance service application on CCF.
```powershell
# Deploy governance service on CCF.
az cleanroom governance service deploy --governance-client "cl-governance"
```

### Contract management
```powershell
# Create a new contract.
$contractId="1221"
$data = '{"hello": "world"}'
az cleanroom governance contract create --data $data --id $contractId

# Update an existing contract.
$version=(az cleanroom governance contract show --id $contractId --query "version" --output tsv)
$data = '{"hello": "world", "foo": "bar"}'
az cleanroom governance contract create --data $data --id $contractId --version $version

# Submitting a contract proposal.
$version=(az cleanroom governance contract show --id $contractId --query "version" --output tsv)
$proposalId=(az cleanroom governance contract propose --version $version --id $contractId --query "proposalId" --output tsv)

# Vote on a contract. If there are multiple members then each member needs to vote before the contract gets accepted.
az cleanroom governance contract vote --id $contractId --proposal-id $proposalId --action accept
```
### Deployment template management
```powershell
# Propose a deployment template which would be used to create an instance of the clean room. CGS 
# treats the structure of the template as opaque.
@"
{
  "key1": "value1",
  "key2": {
    "key3": "value3"
  }
}
"@ > template.json

$proposalId=(az cleanroom governance deployment template propose --contract-id $contractId --template-file ./template.json --query "proposalId" --output tsv)

# Vote on the proposal. If there are multiple members then each member needs to vote before the spec gets accepted.
az cleanroom governance proposal vote --proposal-id $proposalId --action accept

# Once the proposal is accepted below command returns the accepted spec.
az cleanroom governance deployment template show --contract-id $contractId
```

### Clean room policy management
```powershell
# Propose the clean room policy which would be used to identify calls originating from a clean room instance.
# Without this a clean room instance will be unable to get secrets, tokens or insert audit events.
@"
{
  "type": "add",
  "claims": {
    "x-ms-sevsnpvm-is-debuggable": false,
    "x-ms-sevsnpvm-hostdata": "<insert ccepolicy hash value here Eg 73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20>"
  }
}
"@ > policy.json

$proposalId=(az cleanroom governance deployment policy propose --contract-id $contractId --policy-file ./policy.json --query "proposalId" --output tsv)

# Vote on the prposal. If there are multiple members then each member needs to vote before the proposal gets accepted.
az cleanroom governance proposal vote --proposal-id $proposalId --action accept

# Once the proposal is accepted below command returns the accepted policy.
az cleanroom governance deployment policy show --contract-id $contractId
```
### Secrets management
```powershell
# Create/update a secret named `clientsecret`. The command returns `secretId` which is the memberId prefixed secret name that needs to be specified as the secret ID in the clean room specification to fetch this secret.
az cleanroom governance contract secret set --secret-name clientsecret --value "ASDFQ==" --contract-id $contractId
{
  "secretId": "373d3aecb922fc7938c73b8cbef9989bf91c282e621cb8b9598b8ba7f7292820:clientsecret"
}
```

### OIDC Issuer (IdP) management
```powershell
# Enable OIDC Issuer feature in CGS.
$proposalId=(az cleanroom governance oidc-issuer propose-enable --query "proposalId" --output tsv)

# Accept the above proposal. If there are multiple members then each member needs to vote before IdP gets enabled.
az cleanroom governance proposal vote --proposal-id $proposalId --action accept

# Once enabled trigger generation of the OIDC issuer signing key. Any member can trigger key generation.
az cleanroom governance oidc-issuer generate-signing-key

# Get OIDC Issuer configuration details.
az cleanroom governance oidc-issuer show
```

See [how to setup a public, secured OIDC Issuer URL](../../../governance/ccf-app/js/README.md#setup-a-public-secured-oidc-issuer-url-using-azure-blob-storage) to generate the Issuer URL value to use below.
```powershell
# Set tenantId-level Issuer URL that will be exposing the /.well-known/openid-configuration and JWKS documents.
# The tenantId value is automatically picked from the member_data of the member that is invoking this cmdlet.
# This URL will be set as the 'iss' claim value for tokens issued for this tenantId.
# No proposal is required to set tenantId-level Issuer URL.
$issuerUrl= "https://..."
az cleanroom governance oidc-issuer set-issuer-url --url $issuerUrl

# And/or set IssuerUrl at a global level that will be exposing the /.well-known/openid-configuration and JWKS documents.
# This URL will be set as the 'iss' claim value for tokens issued for any tenantId (Any explicit tenantId-level Issuer URL superseeds this value).
$issuerUrl= "https://..."
$proposalId=(az cleanroom governance oidc-issuer propose-set-issuer-url --url $issuerUrl --query "proposalId" --output tsv)

# Accept the above proposal. If there are multiple members then each member needs to vote before issuer URL gets set.
az cleanroom governance proposal vote --proposal-id $proposalId --action accept
```
### Certificate Authority (CA) management
```powershell
# Enable CA feature for a contract in CGS.
$proposalId=(az cleanroom governance ca propose-enable --contract-id $contractId --query "proposalId" --output tsv)

# Accept the above proposal. If there are multiple members then each member needs to vote before IdP gets enabled.
az cleanroom governance proposal vote --proposal-id $proposalId --action accept

# Once enabled trigger generation of the CA key and cert. Any member can trigger key generation.
az cleanroom governance ca generate-key --contract-id $contractId

# Get CA configuration details.
az cleanroom governance ca show --contract-id $contractId
```
### Controlling execution
```powershell
# Disable execution of an accepted contract. Use --action enable to reverse the action.
# Any member can disable/enable contract execution.
az cleanroom governance contract runtime-option set --option execution --action disable --contract-id $contractId

# Check the execution status of an accepted contract.
# Contract execution status remains disabled as long as one or more members have disabled execution.
az cleanroom governance contract runtime-option get --option execution --contract-id $contractId
```

### Controlling Log collection
```powershell
# Propose enabling logging and its collection during cleanroom execution.
$proposalId=(az cleanroom governance contract runtime-option propose --option logging --action enable --contract-id $contractId --query "proposalId" --output tsv)

# Vote on the proposal. If there are multiple members then each member needs to vote before the
# proposal gets accepted.
az cleanroom governance proposal vote --proposal-id $proposalId --action accept

# Check the logging enable/disable status of an accepted contract.
az cleanroom governance contract runtime-option get --option logging --contract-id $contractId

# Propose disable logging and its collection during cleanroom execution. Any member can propose and
# the proposal gets auto-accepted. No voting required.
$proposalId=(az cleanroom governance contract runtime-option propose --option logging --action disable --contract-id $contractId --query "proposalId" --output tsv)
```

### Controlling Telemetry collection
```powershell
# Propose enabling telemetry collection during cleanroom execution.
$proposalId=(az cleanroom governance contract runtime-option propose --option telemetry --action enable --contract-id $contractId --query "proposalId" --output tsv)

# Vote on the proposal. If there are multiple members then each member needs to vote before the
# proposal gets accepted.
az cleanroom governance proposal vote --proposal-id $proposalId --action accept

# Check the telemetry enable/disable status of an accepted contract.
az cleanroom governance contract runtime-option get --option telemetry --contract-id $contractId

# Propose disable telemetry collection during cleanroom execution. Any member can propose and
# the proposal gets auto-accepted. No voting required.
$proposalId=(az cleanroom governance contract runtime-option propose --option telemetry --action disable --contract-id $contractId --query "proposalId" --output tsv)
```
### Events management
```powershell
# Get audit events (if any) emitted by the clean room during contract execution.
az cleanroom governance contract event list --contract-id $contractId --all
{
  "value": [
    {
      "scope": "",
      "id": "cf9a4444",
      "seqno": 120,
      "timestamp": "1712199058629",
      "timestamp_iso": "2024-04-04T02:50:58.629Z",
      "data": {
        "message": "Contract cf9a4444 passed consent check."
      }
    },
    {
      "scope": "",
      "id": "cf9a4444",
      "seqno": 122,
      "timestamp": "1712199059821",
      "timestamp_iso": "2024-04-04T02:50:59.821Z",
      "data": {
        "message": "foo container started for contract cf9a4444."
      }
    },
    {
      "scope": "",
      "id": "cf9a4444",
      "seqno": 124,
      "timestamp": "1712199060985",
      "timestamp_iso": "2024-04-04T02:51:00.985Z",
      "data": {
        "message": "Key was released under contract cf9a4444."
      }
    },
    {
      "scope": "",
      "id": "cf9a4444",
      "seqno": 126,
      "timestamp": "1712199062122",
      "timestamp_iso": "2024-04-04T02:51:02.122Z",
      "data": {
        "message": "foo container finished execution."
      }
    }
  ]
}
```
### Document management
```powershell
# Create a new document under a contract.
$contractId="<AnExistingContractId>"
$documentId="1221"
$data = '{"hello": "world"}'
az cleanroom governance document create --data $data --id $documentId --contract-id $contractId

# Update an existing document.
$version=(Get-Document -id $documentId | jq -r ".version")
$data = '{"hello": "world", "foo": "bar"}'
Create-Document -data $data -id $documentId --contract-id $contractId -version $version
$version=(az cleanroom governance document show --id $documentId --query "version" --output tsv)
$data = '{"hello": "world", "foo": "bar"}'
az cleanroom governance document create --data $data --id $documentId --version $version

# Submitting a document proposal.
$version=(Get-Document -id $documentId | jq -r ".version")
$proposalId=(Propose-Document -version $version -id $documentId | jq -r '.proposalId')
$version=(az cleanroom governance document show --id $documentId --query "version" --output tsv)
$proposalId=(az cleanroom governance document propose --version $version --id $documentId --query "proposalId" --output tsv)

# Vote on a document. If there are multiple members then each member needs to vote before the document gets accepted.
az cleanroom governance document vote --id $documentId --proposal-id $proposalId --action accept
```

### Member addition
```powershell
# A new member "neo" creates a key pair as its member identity and shares the public key
# (neo_cert.pem) with one of the existing members of the consortium.
az cleanroom governance member keygenerator-sh | bash -s -- --name neo
-- Generating identity private key and certificate for participant "neo"...
Identity curve: secp384r1
Identity private key generated at:   neo_privk.pem
Identity certificate generated at:   neo_cert.pem (to be registered in CCF)

# An existing member makes a proposal for adding the new member "neo".
$proposalId=(az cleanroom governance member add --certificate ./neo_cert.pem --identifier neo --query "proposalId" --output tsv)

# Vote on the proposal. If there are multiple existing members then each member needs to vote 
# before the new member proposal gets accepted.
az cleanroom governance proposal vote --proposal-id $proposalId --action accept

# "neo" deploys client-side containers to interact with the governance service as the new member.
az cleanroom governance client deploy `
  --ccf-endpoint "https://<name>.<region>.azurecontainer.io" `
  --signing-cert ./neo_cert.pem `
  --signing-key ./neo_privk.pem `
  --service-cert ./service_cert.pem `
  --name "neo-client"

# "neo" accepts the invitation and becomes an active member in the consortium.
az cleanroom governance member activate --name "neo-client"
```
#### Using member keys stored in Azure Key Vault
Members’s identity certificates and encryption keys can also be created and stored in Azure Key Vault and used with CCF. See [here](https://microsoft.github.io/CCF/main/governance/hsm_keys.html#using-member-keys-stored-in-hsm) for more details.

**Generating identity certificate**
```powershell
# A new member "morpheus" creates a key pair as its member identity and shares the public key
# (morpheus_cert.pem) with one of the existing members of the consortium.
$vaultName = "<vault-name>"
az cleanroom governance member generate-identity-certificate `
  --member-name "morpheus" `
  --vault-name $vaultName
  --output-dir .

# Sample output
Generating identity private key and certificate for participant 'morpheus' in Azure Key Vault...
Identity certificate generated at: ./morpheus_cert.pem (to be registered in CCF)
Identity certificate Azure Key Vault Id written out at: ./morpheus_cert.id

# Azure Key Vault certificate ID
cat ./morpheus_cert.id
https://<vault-name>.vault.azure.net/certificates/morpheus-identity/04b7d45225754cfcaa5827f468b3f444
```

**Generating encryption key**
```powershell
$vaultName = "<vault-name>"
az cleanroom governance member generate-encryption-key `
  --member-name "morpheus" `
  --vault-name $vaultName
  --output-dir .

# Sample output
Generating RSA encryption key pair for participant 'morpheus' in Azure Key Vault...
Encryption public key generated at: ./morpheus_enc_pubk.pem (to be registered in CCF)
Encryption key Azure Key Vault Id written out at: ./morpheus_enc_key.id

# Azure Key Vault encryption key ID
cat ./morpheus_enc_key.id
https://<vault-name>.vault.azure.net/keys/morpheus-encryption/b318faa84ccb4417a3a2c38b49e06ece
```

Governance client can then be deployed using the member signing cert id value.
```powershell
# "morpheus" deploys client-side containers to interact with the governance service as the new member.
az cleanroom governance client deploy `
  --ccf-endpoint "https://<name>.<region>.azurecontainer.io" `
  --signing-cert-id ./morpheus_cert.id `
  --service-cert ./service_cert.pem `
  --name "morpehus-client"
```

## CCF
### Provider management
All `ccf` commands below need a ccf provider deployment. The `provider deploy` command will launch the client-side containers on your local machine to interact with the provider instance.
```powershell
# Deploy client-side containers to interact with the CCF provider.
az cleanroom ccf provider deploy

# To remove the client-side containers for the CCF provider run the below command.
az cleanroom ccf provider remove
```
### Network management
A CCF network can run on several hardware platforms/trusted execution environments, which will have impact on the security guarantees of the service and on how attestation reports are generated and verified.
- AMD SEV-SNP
- Insecure Virtual

See [here](https://microsoft.github.io/CCF/main/operations/platforms/index.html) for more platform details.

CCF network creation supports a collection of initial members. Atleast one initial member is required.
Create the member certificate and private key for the first member by running the below command:
```powershell
az cleanroom governance member keygenerator-sh | bash -s -- --name operator --gen-enc-key
```
This will generate two files: `operator_cert.pem` and `operator_enc_pubk.pem` which are referred to in the subsequent commands.

#### Platform: AMD SEV-SNP using Confidential ACI
**CCF Network creation**  
Below steps deploy a CCF network with 3 CCF nodes. Each node runs in its own confidential container group instance.
```powershell
$location= "westus"
$uniqueString = $((New-Guid).ToString().Substring(0, 8))
$resourceGroup = "ccf-deploy-$uniqueString"
$storageAccountName = "ccf${uniqueString}sa"
az group create --name $resourceGroup --location westus

$networkName = "ccf-network"
$subscriptionId = az account show --query "id" -o tsv

# Create a storage account for using Azure File shares for the CCF nodes.
az storage account create `
    --name $storageAccountName `
    --resource-group $resourceGroup `
    --allow-shared-key-access true

$storageAccountId = az storage account show `
    --name $storageAccountName `
    --resource-group $resourceGroup `
    --query "id" `
    --output tsv

# Add more initial members in the array as required.
@"
[{
    "certificate": "./operator_cert.pem",
    "encryptionPublicKey": "./operator_enc_pubk.pem",
    "memberData": {
        "identifier": "operator",
        "isOperator": true
    }
}]
"@ > members.json

@"
{
    "location": "$location",
    "subscriptionId": "$subscriptionId",
    "resourceGroupName": "$resourceGroup",
    "azureFiles": {
        "storageAccountId": "$storageAccountId"
    }
}
"@ > providerConfig.json

# Command takes a few minutes to execute as it involves container groups creation.
$ccfEndpoint = az cleanroom ccf network create `
    --name $networkName `
    --node-count 1 `
    --members ./members.json `
    --infra-type caci `
    --provider-config ./providerConfig.json `
    --query "endpoint" `
     --output tsv

az cleanroom ccf network show `
    --name $networkName `
    --infra-type caci `
    --provider-config ./providerConfig.json
```
The CCF endpoint value above can then be used to start a `governance client` instance as follows:
```powershell
# Get the service cert.
# Trimming an extra new-line character added to the cert.
$response = (curl -k "$ccfEndpoint/node/network" --silent | ConvertFrom-Json)
$response.service_certificate.TrimEnd("`n") | Out-File "service_cert.pem"

az cleanroom governance client deploy `
  --ccf-endpoint $ccfEndpoint `
  --signing-cert ./operator_cert.pem `
  --signing-key ./operator_privk.pem `
  --service-cert ./service_cert.pem `
  --name "cl-governance"

az cleanroom governance member activate --governance-client "cl-governance"
```
**Opening the network for usage and scale up**  
After creating the 1-node cluster trigger a snapshost and then scale it up to say 3 nodes. Having 
a snapshot leads to a faster CCF node join as it avoids a full ledger replication to the new nodes:
```powershell
# Configure the operator cert in the provider client so that it can sign requests to open the
# network and take snapshots.
az cleanroom ccf provider configure `
    --signing-key ./operator_privk.pem `
    --signing-cert ./operator_cert.pem

az cleanroom ccf network transition-to-open `
    --name $networkName `
    --infra-type caci `
    --provider-config ./providerConfig.json

az cleanroom ccf network trigger-snapshot `
    --name $networkName `
    --infra-type caci `
    --provider-config ./providerConfig.json

az cleanroom ccf network update `
    --name $networkName `
    --node-count 3 `
    --infra-type caci `
    --provider-config ./providerConfig.json
```

**CCF Network deletion**
```powershell
az cleanroom ccf network delete `
    --name $networkName `
    --infra-type caci `
    --provider-config ./providerConfig.json
```

#### Platform: Insecure Virtual using Docker
The insecure virtual platform using Docker is meant for dev/test scenarios to have a CCF endpoint
that runs locally for local onebox environment, CI/CD runs etc.  

**CCF Network creation**
```powershell
$networkName="my-ccf-network"
@"
[{
    "certificate": "./operator_cert.pem",
    "encryptionPublicKey": "./operator_enc_pubk.pem",
    "memberData": {
        "identifier": "operator"
    }
}]
"@ > members.json

az cleanroom ccf network create `
    --name $networkName `
    --node-count 3 `
    --members ./members.json `
    --infra-type virtual

az cleanroom ccf network show `
    --name $networkName `
    --infra-type virtual
```

**CCF Network deletion**
```powershell
$networkName="my-ccf-network"

az cleanroom ccf network delete `
    --name $networkName `
    --infra-type virtual
```
#### Platform: Insecure Virtual using ACI
The insecure virtual platform using standard (non-confidential) ACI is meant for dev/test scenarios 
to have a CCF endpoint that runs in Azure where scenario testing requires the CCF endpoint to be 
available over the Internet.
This setup runs the same insecure virtual CCF image as the Docker environment setup.

**CCF Network creation**
```powershell
$networkName="ccf-network-virtual"
$resourceGroup="<>"
$subscriptionId="<>"
$location="westeurope"
@"
[{
    "certificate": "./operator_cert.pem",
    "encryptionPublicKey": "./operator_enc_pubk.pem",
    "memberData": {
        "identifier": "operator"
    }
}]
"@ > members.json

@"
{
    "location": "$location",
    "subscriptionId": "$subscriptionId",
    "resourceGroupName": "$resourceGroup"
}
"@ > providerConfig.json

# Command takes a few minutes to execute.
az cleanroom ccf network create `
    --name $networkName `
    --node-count 3 `
    --members ./members.json `
    --infra-type virtualaci `
    --provider-config ./providerConfig.json
{
  "endpoint": "https://ccf-network-virtual-lb-kvobzwyuuhvun.westeurope.azurecontainer.io:443",
  "port": 443
}

az cleanroom ccf network show `
    --name $networkName `
    --infra-type caci `
    --provider-config "{'subscriptionId':'$subscriptionId','resourceGroupName':'$resourceGroup'}"
```

**CCF Network deletion**
```powershell
az cleanroom ccf network delete `
    --name $networkName `
    --infra-type virtualaci `
    --provider-config ./providerConfig.json
```

# Links
- Authoring Extensions: https://github.com/Azure/azure-cli/blob/dev/doc/extensions/authoring.md
- Authoring Command Modules: https://github.com/Azure/azure-cli/blob/dev/doc/authoring_command_modules/README.md
- Build your own CLI extension: https://microsoft.github.io/AzureTipsAndTricks/blog/tip200.html

# Generating code under `vendored_sdks`
Follow the steps outlined [here](./vendored_sdks.md).
