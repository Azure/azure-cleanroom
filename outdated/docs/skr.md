# Setting up an RSA key pair for secure key release

## References
https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/key-management   
https://learn.microsoft.com/en-us/azure/confidential-computing/concept-skr-attestation   
https://thomasvanlaere.com/posts/2022/12/azure-confidential-computing-secure-key-release/

## Sample release policy
releasepolicy.json   
Value of `73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20` below is for the allow all ccepolicy.
```json
{
    "version": "1.0.0",
    "anyOf": [
        {
            "authority": "sharedneu.neu.attest.azure.net",
            "allOf": [
                {
                    "claim": "x-ms-sevsnpvm-hostdata",
                    "equals": "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"
                },
                {
                    "claim": "x-ms-compliance-status",
                    "equals": "azure-compliant-uvm"
                },
                {
                    "claim": "x-ms-sevsnpvm-is-debuggable",
                    "equals": "false"
                }
            ]
        }
    ]
}

```

## Steps to create an RSA key pair with above release policy

```pwsh
$hsmName="cleanroom-mshm-test"
$keyName="imagekey"
$releasePolicyFile="<path>\releasepolicy.json"

# Give user access (--assignee) to be able to create keys if not present.
az keyvault role assignment create --hsm-name $hsmName --role "Managed HSM Crypto User" --assignee gsinha@microsoft.com  --scope /keys

# Now create an exportable key with above policy.
az keyvault key create --hsm-name $hsmName --name $keyName --kty RSA-HSM --exportable true --policy $releasePolicyFile

# Download public key for the corresponding key pair.
az keyvault key download --hsm-name $hsmName --name $keyName --encoding PEM --file publickey.pem
```

## Validate that key release is working
Pre-requisites:
- The user-assigned managed identity mentioned in the ARM template below will be attached to the container group so that the containers have the correct access permissions to Azure services and resources. The managed identity needs  *Managed HSM Crypto Officer* and *Managed HSM Crypto User* roles for /keys on the AKV managed HSM instance being used. Confirm that it has this access before proceeding.


Go to Azure portal and click on `deploy a custom template`, then click `Build your own template in the editor`. Copy/paste the contents of [aci-test-skr-arm-template.json](../samples/aci-test-skr-arm-template.json), and update the values of the environment variables for the `test-skr-client-container` and start a deployment. Once deployment is done, to verify the key has been successful released, shell into the `skr-sidecar-container` container and see the log.txt and you should see the following log message: 

```
level=debug msg=Releasing key blob: {doc-sample-key-release}
```

Alternatively, you can shell into the container `test-skr-client-container` and the released key is in keyrelease.out. 