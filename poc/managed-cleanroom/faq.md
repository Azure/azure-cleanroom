# FAQ <!-- omit from toc -->

- [1. Is there an alignment between Fabric Spark and Managed Clean Rooms for Analytics?](#1-is-there-an-alignment-between-fabric-spark-and-managed-clean-rooms-for-analytics)
- [2. How big is the AKS cluster?](#2-how-big-is-the-aks-cluster)
- [3. How big is the CCF cluster and who manages it to maintain quorum?](#3-how-big-is-the-ccf-cluster-and-who-manages-it-to-maintain-quorum)
- [4. What does Zero-Trust guarantee mean here?](#4-what-does-zero-trust-guarantee-mean-here)
- [5. Do all participants need an Azure account?](#5-do-all-participants-need-an-azure-account)
- [6. Who hosts the Clean Room environment and who pays for it?](#6-who-hosts-the-clean-room-environment-and-who-pays-for-it)
- [7. Can a 3P/ISV use this infra to provide a Managed Clean Room offering?](#7-can-a-3pisv-use-this-infra-to-provide-a-managed-clean-room-offering)
- [8. Security FAQ](#8-security-faq)
- [9. Where can I get more technical details about the confidential computing infra. in use?](#9-where-can-i-get-more-technical-details-about-the-confidential-computing-infra-in-use)

## 1. Is there an alignment between Fabric Spark and Managed Clean Rooms for Analytics?

> [!NOTE]
> This discussion is at a brainstorming stage, not vetted with any stakeholders (eg Fabric team).
> As the Managed Clean Room offering matures we'd be in a better position to think about any alignment opportunities with Fabric Spark and evolve the answer here.

There is no obvious alignment at present as Managed Clean Rooms for Analytics and Fabric Spark are two independent offerings, servicing a different set of audiences and requirements.

Fabric Spark targets Spark Application "developers", where end customers are offered a fully managed [Apache Spark pool](https://learn.microsoft.com/en-us/fabric/data-engineering/spark-compute) operated by MS Fabric. Customers define their jobs using [Spark Job Definition](https://learn.microsoft.com/en-us/fabric/data-engineering/create-spark-job-definition#create-a-spark-job-definition-for-pyspark-python) and schedule runs for the same. They have full control on how they author and upload the job definitions.

Managed Clean Room Spark Service is a backend service for workloads targeting Spark Application "users", where end customers just publish datasets and queries without any control on the Spark Application consuming these. It is a highly opinionated and locked down Spark Pool that can only execute pre-defined Spark applications using payload from pre-defined Spark clients through the Clean Room Infrastructure.

Alignment opportunities with the Fabric team will be explored again at a later point in time, especially if they see customer interest in multi party collaboration or confidential computation.

<!-- Points to consider:
- Managed Clean Rooms for Analytics and Fabric Spark are two independent offerings. Both service a different set of audiences and requirements.
- In Fabric Spark clients work with a fully managed [Apache Spark pool](https://learn.microsoft.com/en-us/fabric/data-engineering/spark-compute) operated by MS Fabric.
- Clients create a [Spark Job Definition](https://learn.microsoft.com/en-us/fabric/data-engineering/create-spark-job-definition#create-a-spark-job-definition-for-pyspark-python)
  and schedule runs for it. They author a main definition file in (say) python and upload it.
- In Fabric Spark there is no exposure to the underlying compute for the Spark Pools. They can be running on CVMs but still will not 
  be able to provide any per customer attestation guarantees which can be used to setup any form of secure key release.
- Look into the [data sharing capabilities](https://learn.microsoft.com/en-us/fabric/governance/external-data-sharing-overview) 
  and see how different participants can share data to across tenant boundaries.
  - Open question: Per https://learn.microsoft.com/en-us/fabric/governance/external-data-sharing-overview#considerations-and-limitations 
    "*Shortcuts contained in folders that are shared via external data sharing won't resolve in the consumer tenant.*".
  - Can the [OneLake data sharing link](https://learn.microsoft.com/en-us/fabric/governance/external-data-sharing-create) safely communicated to a clean room environment?
- See if shortcuts to s3 storage needs to be created directly in the consuming tenant and thus participants have to give those details to the tenant hosting the clean room. -->

### Detailed comparison <!-- omit from toc -->

Both [Azure Synapse Spark](https://learn.microsoft.com/en-us/azure/synapse-analytics/spark/apache-spark-overview#apache-spark-in-azure-synapse-analytics-use-cases) and [Fabric Spark](https://learn.microsoft.com/en-us/fabric/data-engineering/spark-compute) offerings expose the Spark compute to developers which can be used to develop and deploy notebooks and applications. See [comparison](https://learn.microsoft.com/en-us/fabric/data-engineering/comparison-between-fabric-and-azure-synapse-spark) between these two offering here.

In comparison to these two, Managed Clean Room for Analytics do not expose general purpose Spark compute for
direct consumption by developers. Rather it is a highly specialized and opinionated Spark compute
deployment that only exposes SQL-query authoring and execution interfaces to the users. Users cannot
create an application/notebooks/Spark Job definitions themselves (in Python, R, Scala etc.) which gets deployed.
Managed Clean Rooms deploys a well-known and publicly maintained application.

The following table compares the offerings across different categories:

|Category | Azure Synapse Spark | Fabric Spark | Managed Clean Rooms for Analytics |
| --- | --- | --- | --- |
| Spark pools | Spark pool <br>- <br>-| [Starter pool](https://learn.microsoft.com/en-us/fabric/data-engineering/configure-starter-pools) / [Custom pool](https://learn.microsoft.com/en-us/fabric/data-engineering/create-custom-spark-pools) <br>[V-Order](https://learn.microsoft.com/en-us/fabric/data-engineering/delta-optimization-and-v-order) <br>[High concurrency](https://learn.microsoft.com/en-us/fabric/data-engineering/configure-high-concurrency-session-notebooks) | Spark pool not exposed for direct consumption <br>- <br>-|
| Spark configurations | Pool level <br>Notebook or Spark job definition level| [Environment level](https://learn.microsoft.com/en-us/fabric/data-engineering/create-and-use-environment) <br>[Notebook](https://learn.microsoft.com/en-us/fabric/data-engineering/how-to-use-notebook) or [Spark job definition](https://learn.microsoft.com/en-us/fabric/data-engineering/spark-job-definition) level| Not exposed
| Spark libraries | Workspace level packages <br>Pool level packages <br>Inline packages | - <br>[Environment libraries](https://learn.microsoft.com/en-us/fabric/data-engineering/environment-manage-library) <br>[Inline libraries](https://learn.microsoft.com/en-us/fabric/data-engineering/library-management)| Not exposed
| Resources | Notebook (Python, Scala, Spark SQL, R, .NET) <br>Spark job definition (Python, Scala, .NET) <br>Synapse data pipelines <br>Pipeline activities (notebook, SJD)| [Notebook](https://learn.microsoft.com/en-us/fabric/data-engineering/how-to-use-notebook) (Python, Scala, Spark SQL, R) <br>[Spark job definition](https://learn.microsoft.com/en-us/fabric/data-engineering/spark-job-definition) (Python, Scala, R) <br>[Data Factory data pipelines](https://learn.microsoft.com/en-us/fabric/data-factory/create-first-pipeline-with-sample-data) <br> [Pipeline activities](https://learn.microsoft.com/en-us/fabric/data-factory/activity-overview) (notebook, SJD)| SQL queries
| Data | Primary storage (ADLS Gen2) <br>Data residency (cluster/region based) | Primary storage ([OneLake](https://learn.microsoft.com/en-us/fabric/onelake/onelake-overview)) <br>Data residency (capacity/region based) | Primary Storage (Azure Blobs) <br>Data residency (location of the Azure storage account)
| Metadata | Internal Hive Metastore (HMS) <br>External HMS (using Azure SQL DB) | Internal HMS ([lakehouse](https://learn.microsoft.com/en-us/fabric/data-engineering/lakehouse-overview)) <br>-| NA |
| Connections | Connector type (linked services) <br>[Data sources](/azure/synapse-analytics/spark/apache-spark-secure-credentials-with-tokenlibrary) <br>Data source conn. with workspace identity | Connector type (DMTS) <br>[Data sources](/power-query/connectors/) <br> - | Connection secrets in CCF <br> Managed Identity Federated Credentials
| Security | RBAC and access control <br>Storage ACLs (ADLS Gen2) <br>Private Links <br>Managed VNet (network isolation) <br>Synapse workspace identity<br>Data Exfiltration Protection (DEP) <br>Service tags <br>Key Vault (via mssparkutils/ linked service) | [RBAC and access control](https://learn.microsoft.com/en-us/fabric/fundamentals/roles-workspaces) <br> [OneLake RBAC](https://learn.microsoft.com/en-us/fabric/onelake/security/data-access-control-model) <br> [Private Links](https://learn.microsoft.com/en-us/fabric/security/security-private-links-overview) <br> [Managed VNet](https://learn.microsoft.com/en-us/fabric/security/security-managed-vnets-fabric-overview) <br> [Workspace identity](https://learn.microsoft.com/en-us/fabric/security/workspace-identity) <br>- <br>[Service tags](https://learn.microsoft.com/en-us/fabric/security/security-service-tags) <br>Key Vault (via [notebookutils](https://learn.microsoft.com/en-us/fabric/data-engineering/microsoft-spark-utilities)) | TBD |
| DevOps | Azure DevOps integration <br>CI/CD (no built-in support) | [Azure DevOps integration](https://learn.microsoft.com/en-us/fabric/cicd/git-integration/intro-to-git-integration)<br> [Deployment pipelines](https://learn.microsoft.com/en-us/fabric/cicd/deployment-pipelines/intro-to-deployment-pipelines) | TBD |
| Developer experience | IDE integration (IntelliJ) <br>Synapse Studio UI <br>Collaboration (workspaces) <br>Livy API <br>API/SDK <br>mssparkutils | IDE integration ([VS Code](https://learn.microsoft.com/en-us/fabric/data-engineering/setup-vs-code-extension)) <br>Fabric UI <br>Collaboration (workspaces and sharing) <br>[Livy API](https://learn.microsoft.com/en-us/fabric/data-engineering/api-livy-overview) <br>[API](/rest/api/fabric/)/SDK <br>[notebooktutils](https://learn.microsoft.com/en-us/fabric/data-engineering/microsoft-spark-utilities) | SQL query authoring UI <br> <br> <br> API/SDK |
| Logging and monitoring | Spark Advisor <br>Built-in monitoring pools and jobs (through Synapse Studio) <br>Spark history server <br>Prometheus/Grafana <br>Log Analytics <br>Storage Account <br>Event Hubs | [Spark Advisor](https://learn.microsoft.com/en-us/fabric/data-engineering/spark-advisor-introduction) <br>Built-in monitoring pools and jobs (through [Monitoring hub](https://learn.microsoft.com/en-us/fabric/data-engineering/browse-spark-applications-monitoring-hub)) <br>[Spark history server](https://learn.microsoft.com/en-us/fabric/data-engineering/apache-spark-history-server) <br>- <br>[Log Analytics](https://learn.microsoft.com/en-us/fabric/data-engineering/azure-fabric-diagnostic-emitters-log-analytics) <br>[Storage Account](https://learn.microsoft.com/en-us/fabric/data-engineering/azure-fabric-diagnostic-emitters-azure-storage) <br>[Event Hubs](https://learn.microsoft.com/en-us/fabric/data-engineering/azure-fabric-diagnostic-emitters-azure-event-hub) | Storage Account |
| Business continuity and disaster recovery (BCDR) | BCDR (data) ADLS Gen2 | [BCDR (data) OneLake](https://learn.microsoft.com/en-us/fabric/onelake/onelake-disaster-recovery) | As per BCDR for the underlying storage configured by the customer. Managed Clean Rooms don't manage customer storage but only access it.

## 2. How big is the AKS cluster?

- AKS cluster is created with a 3-node agent pool setup with VMs of min. size 4 vCPU and 16 GB RAM.
This agent pool is used to run the various supporting services like the Spark Operator, External DNS
and VN2.
- The Spark pods that will perform analysis over large amounts of data are backed by the VN2 node pool.
The number of pods created in the VN2 node pool will depend on how my executor pods are started by
Spark. Each Spark pod will map to 1 CACI container group. This will scale up and down elastically as
executor pods are created and destroyed.
- The size of the Spark pods is TBD. Can be made configurable if need arises.
- The number of VN2 nodes required is to be determined based on scale testing. Guidance per VN2
documentation is that for [every 200 pods](https://github.com/microsoft/virtualnodesOnAzureContainerInstances/blob/1f21897a25e3f460c3c13ef5ac6c71d5cc75538c/Docs/NodeCustomizations.md#scaling-virtual-nodes-up--down)
you to be hosted in virtual node you will need to scale out an additional virtual node replica for it.

## 3. How big is the CCF cluster and who manages it to maintain quorum?

- A 1-node CCF cluster is created and there is no requirement to run at least a 3-node cluster while
keeping (N / 2) + 1 nodes permanently running to maintain quorum.
- Further this 1-node cluster can be stopped and restarted as required by the RP to keep the
running costs low.
- On restart public and private ledger recovery is performed using the ledger files created by the
single node. The ledger files are on an Azure File Share (ZRS account).
- For resumption of CCF the `CCF Recovery Service` is used to perform private ledger recovery. 
The ledger recovery secret is not available outside this confidential service. The CCF network gets 
restarted while preserving all state from the previous run.

See more details [here](../../src/ccf/docs/recovery.md)

## 4. What does Zero-Trust guarantee mean here?

- Consortium management is done by a confidential service. Cloud operator and RP remain outside the trust boundary.
- CCF recovery is performed by a confidential service, so no trust on cloud operator or RP.
  - Failure to perform confidential recovery will have an AME-based JIT flow for performing
  break glass recovery. Same will get audited if performed.
  - For privacy conscious customers the option of AME-based break glass recovery can be disabled if they understand and prefer the consequences of not having it.
- Any customer secrets (like S3 secret key) required to access storage is maintained in CCF private ledger.
  - Only CCF nodes (running in CACI) can read the private ledger.
  - Such secrets are released by CCF only to code running in CACI (Spark pods) on presenting a
  valid SNP attestation report with an expected `hostData` value.
  - RP or cloud operator does not have access to customer secret. They are outside the trust boundary.
- For any MI federated credentials that is setup the IDP running in CCF will give an identity
token to only code that presents a valid SNP attestation report with an expected `hostData` value.
  - Only code that can get an IDP token from CCF can exchange it for the MI access token and gain access to storage.
  - RP or cloud operator cannot get access to MI federated credentials. They are outside the trust boundary.
- All code running in CACI would be open sourced in the public repo and thus available for inspection and review. These containers would be github commit signed, so they can be attested using the github attestation.

## 5. Do all participants need an Azure account?

At least one participant needs to have an Azure account and subscription in which the clean room
 deployment takes place. Other participants in the collaboration need not have an Azure account.
 To invite participants they need to have a Microsoft (work, school or personal) account.

## 6. Who hosts the Clean Room environment and who pays for it?

One of the participant in the collaboration hosts the Clean Room environment in their Azure
subscription. They will get billed for usage as per the costs of the various Azure resources that
get created in their subscription.

## 7. Can a 3P/ISV use this infra to provide a Managed Clean Room offering?

There is a path that has an associated onboarding cost. For now its out of scope.

## 8. Security FAQ

See [here](./security.md).

## 9. Where can I get more technical details about the confidential computing infra. in use?
- Following explains the SEV-SNP stack in detail: https://www.amd.com/content/dam/amd/en/documents/epyc-business-docs/white-papers/SEV-SNP-strengthening-vm-isolation-with-integrity-protection-and-more.pdf
- Explains the attestation details in CACI: https://learn.microsoft.com/en-us/azure/container-instances/confidential-containers-attestation-concepts?utm_source=chatgpt.com
- Verification scheme for CACI: https://github.com/microsoft/confidential-aci-examples/blob/main/docs/Confidential_ACI_SCHEME.md
