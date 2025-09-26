# Clean Room Governance upgrade guide <!-- omit from toc -->

- [Types of updates](#types-of-updates)
- [Clean Room CLI upgrade](#clean-room-cli-upgrade)
- [Clean Room Governance client upgrade](#clean-room-governance-client-upgrade)
- [Clean Room Governance service upgrade](#clean-room-governance-service-upgrade)
  - [Check for available service upgrades](#check-for-available-service-upgrades)
  - [Constitution upgrade](#constitution-upgrade)
  - [js-app upgrade](#js-app-upgrade)

This section describes the day-N experiences of patching and upgrading the governance environment. 
As a clean room consumer, you need to have a plan for keeping your clean room instances up to date.
 This guide addresses the upgrade experiences for the various components involved in running clean rooms.

> [!NOTE]
> This guide assumes that you have finished the [multi-party collaboration sample](../../../../test/onebox/README.md) steps successfully and builds upon that setup to explain the flow.

# Types of updates
There are four types of updates for clean rooms, each one building on the next:

|Update type|Target|
|--|--|
|Clean Room CLI upgrade|`cleanroom` Az CLI extension|
|Governance Client upgrade|Client-side containers running in Docker|
|Governance Service upgrade|CCF `constitution` and `jsapp`|
|CCR containers upgrade|clean room infra. containers running as sidecars in CACI instances|

## Update types <!-- omit from toc -->
- **Clean Room CLI upgrade.** This is the upgrade of the `az cleanroom` extension that is used to perform various clean room actions. A new version would be required to pick up bug fixes, enhancements and also to upgrade the other components to their latest versions.
- **Governance client upgrade.** The governance client instances that run as docker continers on the member's machines can get periodically updated. With new governance service releases one needs to update the client instances to perform governance service upgrades and to take advantage of the latest governance features and enhancements.
- **Governance Service upgrade.** This is the upgrade of two components that back the governance service running on CCF: (a) the custom constitution that is deployed on CCF and (b) the governance service JS application that is installed on CCF. The changes here can be both bug fixes and enhancements.
- **CCR containers upgrade.** The clean room instances that get deployed via CACI have a set of clean room infrastructure containers that run as sidecar containers to support the clean room funcationality. To upgrade these container images new instance of CACI needs to be deployed with the updated container image references.

# Clean Room CLI upgrade
Uninstall/install the cleanroom extension wheel file.
```powershell
az extension remove --name cleanroom
az extension add --source <path/to/latest/whl>
```

# Clean Room Governance client upgrade
See what version is currently running.
```powershell
az cleanroom governance client version --name "consumer-client"
```
```json
{
  "cgs-client": {
    "digest": "sha256:6bbdb78ed816cc702249dcecac40467b1d31e5c8cfbb1ef312b7d119dde7024f",
    "version": "1.0.6"
  }
}
```
Check whether a client instance has upgrades available and if so restart the client instance.
```powershell
az cleanroom governance client get-upgrades --name "consumer-client"
```
The following example output shows the current client version as 1.0.6 and lists the available versions under `upgrades`.
```json
{
  "clientVersion": "1.0.6",
  "upgrades": [
    {
      "clientVersion": "1.0.7"
    }
  ]
}
```

```powershell
# Remove the current client instance.
az cleanroom governance client remove --name "consumer-client"

# Re-deploy the client instance. This uses the latest version that is known to the cli extension.
az cleanroom governance client deploy `
  --ccf-endpoint $ccfEndpoint `
  --signing-cert ./demo-resources/consumer_cert.pem `
  --signing-key ./demo-resources/consumer_privk.pem `
  --name "consumer-client"
```
Each member of the consortium can repeat the above steps to upgrade their respective client instances.

# Clean Room Governance service upgrade
One of the members in the consortium checks for upgrades and kicks off the upgrade process. All 
members need to vote on the upgrade proposal for it to take affect. Building upon the multi-party 
collaboration sample the `consumer` member takes the responsbility of proposing the upgrade.

## Check for available service upgrades
See what version of the service is running on your CCF cluster:
```powershell
az cleanroom governance service version --governance-client "consumer-client"
```
The following example output shows the current constitution and jsapp versions.
```json
{
  "constitution": {
    "digest": "sha256:d1e339962fca8d92fe543617c89bb69127dd075feb3599d8a7c71938a0a6a29f",
    "version": "1.0.6"
  },
  "jsapp": {
    "digest": "sha256:c8f1390531513aeecfa326875ff4e53e9ae9c457e87608870b1a30b7c2f510b1",
    "version": "1.0.6"
  }
}
```
Check whether upgrades are available for your CCF cluster using the `get-upgrades` command:
```powershell
az cleanroom governance service get-upgrades --governance-client "consumer-client"
```
The following example output shows the current constitution and jsapp versions as 1.0.5 and lists the available versions under `upgrades`.
```json
{
  "constitutionVersion": "1.0.5",
  "jsappVersion": "1.0.5",
  "upgrades": [
    {
      "constitutionVersion": "1.0.7"
    },
    {
      "jsappVersion": "1.0.8"
    }
  ]
}
```

## Constitution upgrade
> [!TIP]
> Accepting a `constitution` proposal invalidates any other open proposals. So when performing upgrades of both the constitution and js-app together first propose and accept the constitution change followed by proposing the js-app change.

A member of the consortium proposes the governance service upgrade for the constitution on CCF:
```powershell
az cleanroom governance service upgrade-constitution `
    --constitution-version 1.0.7 `
    --governance-client "consumer-client"
```

**Vote on the proposal**  
Each member in the consortium needs to vote on the upgrade proposals. First fetch the upgrade status to know the proposal Ids for the pending upgrades.
```powershell
az cleanroom governance service upgrade status --governance-client "consumer-client"
```
```json
{
    "proposals": [
      {
        "proposalId": "da4ec2de...",
        "actionName": "set_constitution"
      }
    ]
}
```
Next we vote on the constitution proposal. Each member of the consortium needs to run the below commands:
```powershell
# Get the constitution proposal and vote on it.
$proposalId = az cleanroom governance service upgrade status `
    --governance-client "consumer-client" `
    --query "proposals[?(actionName=='set_constitution')].proposalId" `
    --output "tsv"

az cleanroom governance proposal vote `
  --proposal-id $proposalId `
  --action accept `
  --governance-client "consumer-client"
```

The changes will take affect only after all members have voted their acceptance.

## js-app upgrade
A member proposes the governance service upgrade for the JS app on CCF:
```powershell
az cleanroom governance service upgrade-js-app `
    --js-app-version 1.0.8 `
    --governance-client "consumer-client"
```

**Vote on the proposal**  
Each member in the consortium needs to vote on the upgrade proposals. First fetch the upgrade status to know the proposal Ids for the pending upgrades.
```powershell
az cleanroom governance service upgrade status --governance-client "consumer-client"
```
```json
{
    "proposals": [
      {
        "proposalId": "82267af1...",
        "actionName": "set_js_app"
      }
    ]
}
```
Next we vote on the JS app proposal. Each member of the consortium needs to run the below commands:
```powershell
# Get the jsapp proposal and vote on it.
$proposalId = az cleanroom governance service upgrade status `
    --governance-client "consumer-client" `
    --query "proposals[?(actionName=='set_js_app')].proposalId" `
    --output "tsv"

az cleanroom governance proposal vote `
  --proposal-id $proposalId `
  --action accept `
  --governance-client "consumer-client"
```

The changes will take affect only after all members have voted their acceptance.

<!-- ## CCR containers upgrade -->

<!-- To upgrade the CCR sidecar containers we need to create a new ARM deployment template that refers 
to the updated container images. Points to note:
- This affects the CCE policy value as the policy value will change due
to the change in any of the container image layers.
- Due to the change in CCE policy value the existing KEK(s) that have been setup using SKR with the older CCE policy cannot be used for the updated clean room.
-  New KEK(s) with an updated SKR policy thus needs to be setup.

The below flow captures the sequence of execution. We assume that the consumer from the
multi-party collab. scenario is going to perform the containers upgrade and redeploy the clean room.

### Propose a new contract based off the existing contract
Check whether a contract has upgrades available for the CCR containers that it has been configured with.
```powershell
$existingContractId = "collab1"
az cleanroom governance contract get-upgrades `
    --contract-id $existingContractId `
    --governance-client "consumer-client"
```
The following example output shows the current ccr containers version as 1.0.6 and lists the available versions under `upgrades`.
```json
{
  "ccrVersion": "1.0.6",
  "upgrades": [
    {
      "ccrVersion": "1.0.7"
    }
  ]
}
```
As an upgrade is available one can propose a new contract with the upgrade as follows:
```powershell
# Fetch the existing contract and propose a new instance of it under a new contractId to generate
# upated ARM deployment template and CCE policy.
$contract = az cleanroom governance contract show `
    --contract-id $existingContractId `
    --governance-client "consumer-client"

$contract.data | Out-File ./upgrade-config
az cleanroom config set-ccr-version `
    --version 1.0.7 `
    --cleanroom-config ./upgrade-config

$contractId = "collab1-upgrade"
$data = Get-Content -Raw ./upgrade-config
az cleanroom governance contract create `
    --data "$data" `
    --id $contractId `
    --governance-client "consumer-client"

# Submitting the contract proposal.
$version = (az cleanroom governance contract show `
    --id $contractId `
    --query "version" `
    --output tsv `
    --governance-client "consumer-client")

az cleanroom governance contract propose `
    --version $version `
    --id $contractId `
    --query "proposalId" `
    --output tsv `
    --governance-client "consumer-client"
```
The remaining steps below are the same as that for the mainline multi-party collab scenario. The only difference being that these are performed for a `contractId` that captures the updated clean room specification.

### Agreeing upon the new contract
 Follow [agreeing upon the contract](../../README.md#11-agreeing-upon-the-contract) with the change of `$contractId` value to `collab1-upgrade` as used above.
 ```powershell
 $contractId = "collab1-upgrade"
 # Follow steps in the mainline readme for this section.
 ```
### Propose ARM template and CCE policy
Follow [Propose ARM template, CCE policy and log collection](../../README.md#12-propose-arm-template-cce-policy-and-log-collection) with the change of `$contractId` value to `collab1-upgrade` as used above.
 ```powershell
 $contractId = "collab1-upgrade"
 # Follow steps in the mainline readme for this section.
 ```
### Accept ARM template and CCE policy
Follow [Accept ARM template, CCE policy and logging proposals](../../README.md#13-accept-arm-template-cce-policy-and-logging-proposals) with the change of `$contractId` value to `collab1-upgrade` as used above.

### Setup access for the updated clean room
Follow [Setup access for the clean room](../../README.md#14-setup-access-for-the-clean-room) with the change of `$contractId` value to `collab1-upgrade` as used above.

### Deploy updated clean room
Follow [Deploy clean room](../../README.md#15-deploy-clean-room) with the change of `$contractId` value to `collab1-upgrade` as used above. -->
