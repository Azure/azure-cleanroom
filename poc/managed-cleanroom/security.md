# Security FAQ <!-- omit from toc -->

- [CCF](#ccf)
  - [1. What does the CCE policy for the CCF deployment measure?](#1-what-does-the-cce-policy-for-the-ccf-deployment-measure)
  - [2. If deployments in different tenants have the same CCE policy measurement then isn't that an issue?](#2-if-deployments-in-different-tenants-have-the-same-cce-policy-measurement-then-isnt-that-an-issue)
  - [3. How does one establish trust with the CCF endpoint?](#3-how-does-one-establish-trust-with-the-ccf-endpoint)
  - [4. What if the `azurecontainer.io` endpoint gets hijacked/reused for a different deployment?](#4-what-if-the-azurecontainerio-endpoint-gets-hijackedreused-for-a-different-deployment)
  - [5. Who all can perform recovery of the CCF instance and how are their recovery keys managed?](#5-who-all-can-perform-recovery-of-the-ccf-instance-and-how-are-their-recovery-keys-managed)
  - [6. What does the CCE policy for the Confidential Recovery Service measure?](#6-what-does-the-cce-policy-for-the-confidential-recovery-service-measure)
  - [7. Is there any deployment/tenant specific values in the CCE policy for the recovery service?](#7-is-there-any-deploymenttenant-specific-values-in-the-cce-policy-for-the-recovery-service)
  - [8. So any deployment of a recovery service can try to get the keys for any other deployment via SKR, as all can present identical attestation collateral to AKV/MAA. Isn't that an issue?](#8-so-any-deployment-of-a-recovery-service-can-try-to-get-the-keys-for-any-other-deployment-via-skr-as-all-can-present-identical-attestation-collateral-to-akvmaa-isnt-that-an-issue)
  - [9. What if MAA/AKV goes rogue?](#9-what-if-maaakv-goes-rogue)
  - [10. What can happen if someone starts a malicious SNP that produces a valid host\_data but with an arbitrary measurement (effectively arbitrary code)? Or a formerly valid but now exploited UVM, which can be used to claim arbitrary host\_data?](#10-what-can-happen-if-someone-starts-a-malicious-snp-that-produces-a-valid-host_data-but-with-an-arbitrary-measurement-effectively-arbitrary-code-or-a-formerly-valid-but-now-exploited-uvm-which-can-be-used-to-claim-arbitrary-host_data)
- [AKS cleanroom environment](#aks-cleanroom-environment)
  - [1. What does the CCE policy for the spark analytics agent and frontend measure?](#1-what-does-the-cce-policy-for-the-spark-analytics-agent-and-frontend-measure)
  - [2. How does one establish trust with the spark analytics agent HTTPS endpoint?](#2-how-does-one-establish-trust-with-the-spark-analytics-agent-https-endpoint)
  - [3. How is trust established between spark analytics agent and frontend?](#3-how-is-trust-established-between-spark-analytics-agent-and-frontend)
  - [4. How is the SKR setup by customers on the CCE policy for the spark analytics agent consumed?](#4-how-is-the-skr-setup-by-customers-on-the-cce-policy-for-the-spark-analytics-agent-consumed)
  - [5. If the CCF endpoint that the spark analytics agent communicates with is hijacked won't that transfer secrets from customer AKV into the rogue CCF instance?](#5-if-the-ccf-endpoint-that-the-spark-analytics-agent-communicates-with-is-hijacked-wont-that-transfer-secrets-from-customer-akv-into-the-rogue-ccf-instance)
- [Commit signing](#commit-signing)
  - [1. How does one tie the CCE policy values for the deployed instances with the corresponding code in GitHub?](#1-how-does-one-tie-the-cce-policy-values-for-the-deployed-instances-with-the-corresponding-code-in-github)

# CCF
## 1. What does the CCE policy for the CCF deployment measure?
The exact CCE policy generation inputs are available [here](/build/templates/ccf-network-policy/ccf-network-policy.json). The policy measures:
- The container image layers for the `cchost` container and other sidecars containers that get started.
- Command line used to launch the above containers.
- Allowed environment variable names, but not their values.

Note that:
-  The CCF configuration file (which is used to configure the `cchost` process) contents are not measured. The configuration file is outside the TCB so does not need to be measured.
-  The values for the env variables are not security sensitive settings.

Not capturing the environment variable values and the CCF configuration file contents in the policy allows for flexibility in generation and updates of the values without affecting the policy.

There are also no deployment/tenant specific values that get measured the policy. So two separate deployments of CCF would have the same `host_data` value in their SNP attestation report.

> [!CAUTION]
> - At least log level of CCF should be measured in policy with default to info? As ability to redeploy with debug logging should affect policy. Also scan other configuration settings in the cchost config files for similar concerns.

## 2. If deployments in different tenants have the same CCE policy measurement then isn't that an issue?
Not having any deployment/tenant specific values in the CCE policy does not compromise any confidentiality assurances of a CCF deployment. The policy measures the complete set of processes running in the UVM environment which handle sensitive data. Further:
 - Confidential data, like any secrets in the CCF private ledger, are encrypted with ledger encryption keys that is not available outside the UVM environment.
 - SSL traffic termination for CCF REST APIs (both governance and app endpoints) happens within the UVM and handled by the `cchost` process directly.
 - SSL traffic termination for any CCF Recovery Agent REST APIs happens within the UVM and handled by the `envoy` sidecar which then forwards the call (https->http) to the `recovery-agent` sidecar.

Also note that the CCE policy value for the CCF environment is not used to setup any Secure Key Release (SKR) via MAA/AKV. So there is no scenario where CCF deployments for different purposes could theoretically attempt to access each other's secrets as both have the same CCE policy value so could present identical attestation collateral to MAA/AKV for SKR.

## 3. How does one establish trust with the CCF endpoint?
A **cleanroom environment** communicates with a CCF endpoint after establishing trust as follows:
1. The cleanroom environment is configured with the expected CCF endpoint fqdn (an *.azurecontainer.io* address), CCF cert discovery endpoint fqdn, `host_data`, `constitution` digest and `js app` bundle digest values for the CCF instance. These values are measured in the CCE policy of the cleanroom environment.
1. The cleanroom first connects over HTTPS on the CCF cert discovery endpoint (`fqdn:444/network/report`) to obtain an SNP attestation report along with other collateral (more details below). At this point the CA cert to use for SSL connections with CCF is not yet known so an *insecure* SSL connection (ie skipping TLS cert verification) is used.
1. The cleanroom then verifies the SNP attestation report. Fetching of the report over the insecure SSL connection is not an issue as SNP report validation is agnostic to the mechanism via which a report was received.
1. On successful verification of the report it then checks that the `host_data` value in the report matches the expected CCF `host_data` value the cleanroom was configured with.
1. If the `host_data` value in the report matches the expected value then that confirms the integrity (ie what code is running in the UVM) of the environment that generated this report.
1. Along with the attestation report the `/network/report` endpoint also returns the following [collateral data](https://github.com/azure-core/azure-cleanroom/blob/c58a0acfe7efcbda19ebde57577514c658977ad2/src/ccf/recovery/ccf-recovery-agent/Controllers/ServicesController.cs#L89):
   - The CCF service cert PEM that the `cchost` process is using for its SSL server
   - The digest of the `constitution` deployed in CCF
   - The digest of the `js app` bundle deployed in CCF
  
   The sha256 hash value of the above collateral is captured as the `report_data` value while generating the attestation report.
1. The cleanroom next verifies that the hash of the above collateral returned by `/network/report` endpoint matches the attestation report's `report_data` value.
1. If the `report_data` value in the report matches the expected value then that concludes the discovery of the CA certificate (aka service cert) to use for establishing secure SSL connections with the CCF endpoint.
1. It then compares the digest values of the `constitution` and `js app` bundle present in the collateral with the expected values that the cleanroom was configured with.
   - Validating the constitution and js app digest values ensures that only code that has been reviewed/signed off and its expected digest value calculated is running to back the CCF governance (via constitution) and the CCF application endpoints (via js app bundle).
   - Having arbitrary code deployed for the constitution or the application endpoint would compromise the integrity of the CCF environment.
1. If the constitution and js app bundle digest values also match then the cleanroom is assured about the overall integrity of the CCF environment.

A **client outside the cleanroom** can establish trust with a CCF endpoint as follows:
1. The client can be directly configured with the CCF service cert (obtained by some external mechanism) to create secure connections to a CCF endpoint and then communicate with its governance and/or app endpoints.
1. The client can also follow the same protocol as a cleanroom and invoke the `/network/report` endpoint to discover and verify the CCF service cert, constitution and js app bundle values and then then communicate with its governance and/or app endpoints.

The above approaches are implemented for the cleanroom [in ccr-governance](https://github.com/azure-core/azure-cleanroom/blob/c58a0acfe7efcbda19ebde57577514c658977ad2/src/governance/sidecar/CcfClientManager.cs#L77) and for a client outside the cleanroom [in cgs-client](https://github.com/azure-core/azure-cleanroom/blob/c58a0acfe7efcbda19ebde57577514c658977ad2/src/governance/client/Controllers/WorkspacesController.cs#L160) via the [DownloadServiceCertificatePem](https://github.com/azure-core/azure-cleanroom/blob/c58a0acfe7efcbda19ebde57577514c658977ad2/src/internal/restapi-common/Certs/ServiceCertLocator.cs#L45) and [ExtractCertificate](https://github.com/azure-core/azure-cleanroom/blob/c58a0acfe7efcbda19ebde57577514c658977ad2/src/internal/restapi-common/Certs/CcfServiceCertLocator.cs#L20) methods. SNP attestation report verification logic is captured in the [VerifySnpAttestation](https://github.com/azure-core/azure-cleanroom/blob/c58a0acfe7efcbda19ebde57577514c658977ad2/src/internal/Attestation/snp/SnpReport.cs#L102) method.

## 4. What if the `azurecontainer.io` endpoint gets hijacked/reused for a different deployment?
The FQDN used to connect a CCF instance (be it the `cchost` or `recovery-agent` processes behind the endpoint) is not a security sensitive value that needs to be protected. Per the steps laid down above for establishing trust the exact fqdn value does not have a role to play. Even though a cleanroom environment measures the fqdn values in its policy its not strictly required to do so.

## 5. Who all can perform recovery of the CCF instance and how are their recovery keys managed?
CCF recovery can be performed by members of the CCF who have added their [encryption keys](https://microsoft.github.io/CCF/main/governance/adding_member.html#generating-member-keys-and-certificates) and are either a recovery participant or a recovery owner. 

**Unmanaged cleanroom offering (OSS)**  
The cleanroom offering supports configuring a confidential recovery service where in the confidential recovery service member acts a recovery owner. See [recovery service details](/src/ccf/docs/recovery.md) about how exactly the confidential recovery service is setup to manage its recovery key.

**Managed cleanroom offering (1P Microsoft)**  
The managed cleanroom offering will configure both a confidential recovery service exactly as per the [recovery service details](/src/ccf/docs/recovery.md) and would also add an additional member, referred to as the `Consortium Manager` that will also act as a recovery owner.

This additional `Consortium Manager` member will be used to perform break glass recovery using its member recovery key if for some reason the confidential recovery service is not able to come up. The `Consortium Manager` member and encryption keys will be kept in AKV and protected via an SKR policy. If the `Consortium Manager` service is itself not able to come up and get access to the keys (via SKR) then as a last resort the member key can also be retrieved via JIT in a SAW environment (no SKR). Exact details of the JIT/SAW environment are TBD.

## 6. What does the CCE policy for the Confidential Recovery Service measure?
The exact CCE policy generation inputs are available [here](/build/templates/ccf-recovery-service-policy/ccf-recovery-service-policy.json). The policy measures:
- The container image layers for the `ccf-recovery-service` container and other sidecars containers that get started.
- Command line used to launch the above containers.
- An environment variable capturing the expected `host_data` value for nodes of the CCF network that are allowed to communicate with the recovery service.
- Other allowed environment variable names, but not their values.

## 7. Is there any deployment/tenant specific values in the CCE policy for the recovery service?
There are no deployment/tenant specific values in the policy.

## 8. So any deployment of a recovery service can try to get the keys for any other deployment via SKR, as all can present identical attestation collateral to AKV/MAA. Isn't that an issue?
- Azure RBAC needs to be configured to allow a recovery service instance to be able to access an AKV instance so that it can request a key release. So that is the first line of defense.
- Even if one treats Azure RBAC as outside the TCB and a recovery service instance is able to successfully authenticate with AKV, SKR will still happen based on the `host_data` value that has been configured as the SKR policy.
- The `host_data` value measurement ensures that the keys will get released only to trusted code running in the UVM. It makes no difference from a security perspective where that code is running and in whose deployment/tenant.

One can effectively look at it as *free compute* that someone is running by hosting a recovery service in their environment with the exact same code that is trusted for key release.

## 9. What if MAA/AKV goes rogue?
The usage of MAA/AKV in the context of SKR in CCF is as follows:

**CCF network deployment**  
There is no SKR setup for a CCF network deployment so MAA/AKV have no role to play for a CCF network.

**CCF recovery service deployment**  
The CCF recovery service uses AKV/MAA for SKR of the recovery service member and encryption keys that are used to recover a CCF network.

**Consoritum Manager service (1P Microsoft)**  
The `Consortium Manager` service uses AKV/MAA for SKR of the member and encryption keys that are used to perform member actions (like create and accept proposals) or recover the CCF network.

At this point there is no mitigation for a compromised MAA/AKV environment.  It would be a customer choice depending on whether they believe their specific MAA/AKV instances have been attacked and secrets leaked, is to create fresh instances of the services with new secrets and thus recreate the entire setup.

## 10. What can happen if someone starts a malicious SNP that produces a valid host_data but with an arbitrary measurement (effectively arbitrary code)? Or a formerly valid but now exploited UVM, which can be used to claim arbitrary host_data?
Assuming the default UVM measurement would get updated as part of addressing the vulnerability, the following needs to happen:

- Trigger recovery of any running CCF network so that new instances are brought up on the newer UVM. One needs to follow the usual member recovery protocol to do this as any confidential recovery service instances that are restarted and pick up the newer UVM will have their SKR automatically fail due to change in UVM measurement.
- Create a new conf. recovery service instance (which should now be using the newer UVM) and setup conf. recovery for the recovered CCF network using this instance. The confidential recovery service's SKR policy will now be using the new UVM measurement.

There are 2 scenarios where this matters:

**Unmanaged cleanroom offering (OSS)**  
- Collaborators will need to recover the CCF network through the usual member recovery protocol.
- Post recovery they need to again enable confidential recovery for the recovered network.

**Managed cleanroom offering (1P Microsoft)**  
- The Consortium Manager would support upgrade flows for "notified" UVM changes where secrets will be re-imported by the "old" version with the expected measurement of the "new" version.
- If the team is not notified and the infra is abruptly changed underneath, then the SAW/JIT fallback flow would have to be executed to perform initial recovery following which the Consortium Manager generates a new certificate inside the TEE and re-configures the consortium.

The above flow can be followed as the general "found vulnerability, create patch and push update" process. A subsequent step, which would be a customer choice depending on whether they believe their specific instance of the CCF or recovery service has been attacked and secrets leaked, is to not even attempt recovery but create fresh instances of the services with new secrets and thus recreate the entire setup.

# AKS cleanroom environment
## 1. What does the CCE policy for the spark analytics agent and frontend measure?
The exact CCE policy generation inputs for the analytics agent are available [here](/build/templates/cleanroom-spark-analytics-agent-policy/cleanroom-spark-analytics-agent-policy.json). The policy measures:
- The container image layers for the `cleanroom-spark-analytics-agent` container and other sidecars containers that get started.
- Command line used to launch the above containers.
- An environment variable named `SPARK_FRONTEND_SNP_HOST_DATA` capturing the expected `host_data` value for spark frontend service that will run in the AKS cluster.
- Various other environment variables and their values.

The exact CCE policy generation inputs for the frontend service are available [here](/build/templates/cleanroom-spark-frontend-policy/cleanroom-spark-frontend-policy.json). The policy measures:
- The container image layers for the `cleanroom-spark-frontend` container and other sidecars containers that get started.
- Command line used to launch the above containers.
- Various allowed environment variable names, but not their values as most are not security sensitive.

## 2. How does one establish trust with the spark analytics agent HTTPS endpoint?
A client connecting to the spark analytics agent endpoint can establish trust with the environment as follows:

1. The client connects over HTTPS on the analytics agent endpoint (a *.cloudapp.azure.com* address) using the CGS CA cert (see `CA management` [here](/src/tools/azure-cli-extension/cleanroom/README.md#certificate-authority-ca-management)) as the root cert for SSL cert chain verification.
1. If the SSL connection succeeds then that ensures that the environment being connected to has been approved by CGS, as the SSL cert presented by the agent endpoint is signed by CGS root CA cert.

The SSL certificate for the analytics agent is generated by [CGS](/src/governance/ccf-app/js/README.md) as follows:
1. As part of initial setup CGS is configured with the clean room policy which has the `host_data` values for the cleanroom environment(s) that have been approved for usage.
1. When the analytics agent instance starts up it requests CGS (running in CCF) to issue a certificate (see [generateEndorsedCert](/src/governance/ccf-app/js/src/endpoints/ca/cakey.ts)) while presenting an SNP attestation report for its environment.
  > [!TIP]
  > See [How does one establish trust with the CCF endpoint?](#3-how-does-one-establish-trust-with-the-ccf-endpoint) for how the cleanroom environment (ie spark analytics agent) also establishes trust with the CCF endpoint that is hosting CGS.
1. If the attestation report presented by the agent contains one of the allowed values for `host_data` then CGS generates a CGS CA endorsed SSL certificate and returns the same to the analytics agent.
1. The analytics agent starts the `envoy` sidecar to run the SSL server using the generated cert.

Thus if the SSL connection with the analytics agent endpoint succeeds using the CGS CA certificate then the client is assured of the integrity of the environment its connecting to which further implies that the SSL connection from the client to the analytics agent will terminate within the analytics agent cleanroom environment. So any communication with the analytics agent endpoint has end-to-end protection.

## 3. How is trust established between spark analytics agent and frontend?
1. The analytics agent is configured with the frontend endpoint fqdn (a *.svc* k8s service address) and the `host_data` value for the frontend instance. These values are measured in the CCE policy of the analytics agent.
1. The agent connects over HTTPS on the frontend endpoint (`fqdn:443/report`) to obtain an SNP attestation report along with other collateral (more details below). At this point the CA cert to use for SSL connections with the frontend is not yet known so an *insecure* SSL connection (ie skipping TLS cert verification) is used.
1. The agent then verifies the SNP attestation report. Fetching of the report over the insecure SSL connection is not an issue as SNP report validation is agnostic to the mechanism via which a report was received.
1. On successful verification of the report it then checks that the `host_data` value in the report matches the expected frontend `host_data` value the agent was configured with.
1. If the `host_data` value in the report matches the expected value then that confirms the integrity (ie what code is running in the UVM) of the environment that generated this report.
1. Along with the attestation report the `/report` endpoint (see [getFrontendReport](/src/workloads/cleanroom-spark-frontend/src/main.py)) also returns the following collateral data:
   - The service cert PEM for the self-signed certificate that the frontend generated and is using for its SSL server
  
   The sha256 hash value of the above collateral is captured as the `report_data` value while generating the attestation report.
1. The agent next verifies that the hash of the above collateral returned by `/report` endpoint matches the attestation report's `report_data` value.
1. If the `report_data` value in the report matches the expected value then that concludes the discovery of the CA certificate to use for establishing secure SSL connections with the frontend endpoint.

The above approach is implemented via [SparkFrontendServiceCertLocator](https://github.com/azure-core/azure-cleanroom/blob/develop/src/workloads/cleanroom-spark-analytics-agent/SparkFrontendServiceCertLocator.cs) and [DownloadServiceCertificatePem](https://github.com/azure-core/azure-cleanroom/blob/c58a0acfe7efcbda19ebde57577514c658977ad2/src/internal/restapi-common/Certs/ServiceCertLocator.cs#L45).

## 4. How is the SKR setup by customers on the CCE policy for the spark analytics agent consumed?
See [Flow - Execute Workload](/poc/managed-cleanroom/execute-workload.md#prepare-phase) for the exact sequence of steps involving the spark analytics agent accessing customer's secrets via SKR and transferring them over into CCF's private ledger. This is done so that the driver/executor pods that will be executing Spark SQL queries have access to the same and thus able to read encrypted data from the customer's environment.

The driver/executor pods executing the spark queries have CCE policies are dynamic ie they are specific to each query. The policy values for a query in question are known only after the query text and the dataset descriptors have been finalized by the collaborators. When the analytics agent transfers secrets from customer's AKV to CCF's private ledger to setup query execution, it sets a per secret cleanroom policy where the policy captures the `host_data` values for the driver and executor pods that will execute the approved query (identified by a query ID). Only driver/executor pods that can present an SNP attestation report with the `host_data` values as per the per secret cleanroom policy will be able to get access to these secrets.

## 5. If the CCF endpoint that the spark analytics agent communicates with is hijacked won't that transfer secrets from customer AKV into the rogue CCF instance?
Apart from following the steps laid out to establish trust with a CCF instance by the cleanroom [above](#3-how-does-one-establish-trust-with-the-ccf-endpoint), before transferring any secrets into CCF's private ledger the analytics agent also checks that the consortium comprises of only trusted member(s). The members of the consortium have the ability to add/remove more members or recover the CCF network so its vital that the consortium membership is as per the prior agreement. This ensures there are no unexpected (rogue) members in the CCF environment which could try to perform recovery and get to the secrets.

The attack vector we are interested in one where the endpoint URL is hijacked at the time of clean room execution to make it use a "rogue" CCF for staging the secrets and the recovery operator of the rogue CCF decrypting the private ledger to access the secrets. For this, it is necessary for the clean room to check that the CCF it is talking to is the "original" CCF that the user has joined, and not an impersonator, which can only be achieved by adding some payload that is a) inspected by users at the time of accepting the invitation in the clean room contract, and b) inspected by the cleanroom at the time of storing secrets. The only meaningful payload (that we could identify) is the member list, which turns out to be particularly effective.

With respect to who are the expected members for a CCF setup we have the following scenarios:

**Unmanaged cleanroom offering (OSS)**  
Its up to the collaborating parties setting up the environment to decide the membership and who can perform recovery and the same can be validated by the analyticsa gent before transferring any secrets.

**Managed cleanroom offering (1P Microsoft)**  
The managed cleanroom offering will configure a `Confidential Recovery Service` member also the `Consortium Manager` member. These are the only two members that are expected to perform recovery for a managed consortium. These membership details can be supplied as input for validation by the analytics agent before transferring any secrets.

# Commit signing
## 1. How does one tie the CCE policy values for the deployed instances with the corresponding code in GitHub?


Each container image can be cryptographically linked to the exact source code that produced them using [GitHub Attestation](https://github.blog/news-insights/product-news/introducing-artifact-attestations-now-in-public-beta/). These attestation are published per container during a release [here](/.github/actions/attest-artefact/action.yml).

**What does the attestation capture?**

Each attestation includes a signed [SLSA-compliant](https://slsa.dev/spec/v1.0/provenance) provenance statement that records:
- The **digest** of the artifact
- The **timestamp** of creation
- The **workflow metadata**:
  - GitHub workflow file name
  - Job and run information
- The **commit SHA** and **repository URL**

Attestations are persisted to a **tamper-evident ledger**, providing transparency and integrity guarantees.

**Tracing a container image back to its source**

> [!CAUTION]
> Attestation based verifications are WIP for managed cleanrooms.
 
The unmanaged cleanroom offering deploys cleanrooms on C-ACI instances. For such cases the following sequence of steps may be followed to trace a container image back to its source:

1. Get the list of container images:
   
   **For unmanaged cleanroom offering (OSS) with cleanrooms on C-ACI**
   ```pwsh
   $resourceGroup = "<resource-group-name>"
   $name = "<name-of-caci>"

   $images =  az container show `
      --name $name `
      --resource-group $resourceGroup `
      --query "containers[].image" `
      --output tsv | uniq

   $initContainerImages = az container show `
      --name $name `
      --resource-group $resourceGroup `
      --query "initContainers[].image" `
      --output tsv | uniq

   $images += $initContainerImages
   ```

   **For the managed cleanroom offering (1P Microsoft)**

   ```pwsh
   $resourceGroup = "<resource-group>"
   $clusterName = "<aks-cluster-name>"

   # Get credentials for the AKS cluster
   az aks get-credentials --resource-group $resourceGroup --name $clusterName

   $namespace = "cleanroom-spark-analytics-agent"
   $pods = kubectl get pods -n $namespace -o json | ConvertFrom-Json

   $images = @()

   foreach ($pod in $pods.items) {
      # Init containers
      if ($pod.spec.initContainers) {
         foreach ($init in $pod.spec.initContainers) {
               $images += $init.image
         }
      }

      # App containers
      if ($pod.spec.containers) {
         foreach ($container in $pod.spec.containers) {
               $images += $container.image
         }
      }
   }

   $images = $images | uniq
   ```
2. Fetch commit IDs for all the images after fetching Github attestations:
   ```pwsh
   foreach ($image in $images) {
    $attestations = gh attestation verify "oci://$image" `
        --repo "Azure/azure-cleanroom" `
        --format json | ConvertFrom-Json

    $subject = $image.Split("@")[0]
    $attestation = $attestations[0] | Where-Object {$_.verificationResult.statement.subject.name -eq $subject}

    $commit = $attestation.verificationResult.statement.predicate.buildDefinition.resolvedDependencies.digest.gitCommit
    Write-Host -ForegroundColor Green "Image $image is built off the commit $commit."
   }
   ```

A more detailed sample for verifying attestation can be found [here](https://github.com/Azure-Samples/azure-cleanroom-samples/blob/main/scripts/common/assert-cleanroomattestation.ps1).

**TODO**

1. At present, CCE policies are generated as documents that are pushed to MCR and later pulled at runtime. The pulled artifact, however, is not validated for correctness.

    One option to improve this is to embed the policy YAML files generated at each release directly into the Azure CLI and Spark frontend containers. In production scenarios, these bundled YAML files could be used, while the existing push–pull mechanism may remain available as a test hook, controlled via environment variables.

    A consideration with this approach is that it introduces an ordering requirement in the build–release pipeline, where the Azure CLI and Spark frontend containers need to be built last.

2. Another area of exploration is the use of **reproducible builds** as an alternative to `gh attestation`. The latter brings a non-confidential system into the trust boundary. For reference, see how CCF handles reproducible builds [here](https://microsoft.github.io/CCF/main/audit/reproducible_build.html).

3. The `az confcom` extension, with the `--diff` option, can be used to verify that an ARM template or deployment YAML includes a compliant CCE policy. If all images in the template are generated from the same commit, and the template contains a compliant CCE policy, this provides a way to establish that the policy itself ties back to that same git commit.
