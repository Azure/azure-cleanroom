# Configure Workload Sequence Diagrams

Detailed sequence diagrams for interactions between various components as part of configuring a workload (publishing datasets and queries).

## Publish Dataset - Azure Collaborator

```mermaid
sequenceDiagram
title Publish Dataset

box purple Contosso Subscription
  participant AD as Azure AD
  participant Blob as Azure Blob Storage
  participant AKV as Azure Key Vault
end

actor User as Contosso

box brown AME Services
    participant FE as Workload<br>Frontend<br>(Dataplane API)
end

box green Litware Subscription<br>Confidential
    participant CGS as Clean Room<br>Governance
end

User->>+AD: Create Managed Identity
AD--)-User: managed_identity

User->>Blob: Allow Access {managed_identity}
User->>AKV: Allow Access {managed_identity}

User->>+FE: Get Clean Room Details
FE->>CGS: Get Clean Room Identity
CGS--)FE: cleanroom_identity
FE->>CGS: Get Clean Room Security Policy
CGS--)FE: cleanroom_policy
FE--)-User: {cleanroom_identity,<br>cleanroom_policy}

User->>+AD: Set Federation {managed_identity, cleanroom_identity}
AD--)-User: <br>
User->>+AKV: Set Key with SKR<br>{encryption_key,<br>cleanroom_policy}
AKV--)-User: encryption_key_id

User->>+FE: Publish Dataset<br>{storage_details,<br>managed_identity,<br>encryption_key_id,<br>schema}
note over FE: Generate<br>Dataset Descriptor
FE->>CGS: Propose Document<br>{Contosso_Dataset}<br>{approvers: Contosso}
CGS--)FE: <br>
FE->>CGS: Accept Document<br>{Contosso_Dataset}<br>{Contosso Identity}
CGS--)FE: <br>
FE--)-User: 

```

## Publish Dataset - Off Azure Collaborator

```mermaid
sequenceDiagram
title Publish Dataset

participant S3 as AWS S3
actor User as Contosso

box brown AME Tenant
    participant FE as Workload<br>Frontend<br>(Dataplane API)
end

box green Litware Subscription<br>Confidential
    participant CGS as Clean Room<br>Governance
end

User->>S3: Get Connection Secret
S3--)User: connection_secret

User->>+FE: Publish Dataset<br>{storage_details,<br>connection_secret,<br>encryption_key,<br>schema}

FE->>CGS: Get Clean Room Security Policy
CGS--)FE: cleanroom_policy
FE->>CGS: Set Secret {connection_secret, cleanroom_policy}
CGS--)FE: <br>
FE->>CGS: Set Secret {encryption_key, cleanroom_policy}
CGS--)FE: <br>

note over FE: Generate Dataset Descriptor
FE->>CGS: Propose Document<br>{Contosso_Dataset}<br>{approvers: Contosso}
CGS--)FE: <br>
FE->>CGS: Accept Document<br>{Contosso_Dataset}<br>{Contosso Identity}
CGS--)FE: <br>
FE--)-User: 

```

## Publish Query

```mermaid
sequenceDiagram
title Publish Query

actor Owner as Litware
actor User2 as Contosso
actor User1 as Fabrikam

box brown AME Services
    participant FE as Workload<br>Frontend<br>(Dataplane API)
end

box green Litware Subscription<br>Confidential
    participant CGS as Clean Room<br>Governance
end

User1->>+FE: Create Query<br>{query, contosso_dataset, litware_dataset}
note over FE: Generate Query Document
FE->>CGS: Create Document<br>{id: fabrikam_query}
note over CGS: fabrikam_query<br>State: Draft
CGS--)FE: 
FE--)-User1: fabrikam_query

Owner->>+FE: Update Query<br>{fabrikam_query}
FE->>CGS: Update Document<br>{id: fabrikam_query}
note over CGS: fabrikam_query<br>State: Draft
CGS--)FE: 
FE--)-Owner: <br>

User1->>+FE: Propose Query<br>{fabrikam_query}
note over FE: Determine Dataset Owners
FE->>CGS: Propose Document<br>{id: fabrikam_query}<br>{approvers: Contosso, Litware}
note over CGS: fabrikam_query<br>State: Not Approved<br><br>Pending:<br>Contosso, Litware<br><br>Accepted:<br>
CGS--)FE: 
FE--)-User1: <br>

Owner->>+FE: Approve Query<br>{fabrikam_query}
FE->>CGS: Accept Document<br>{id: fabrikam_query}<br>{Litware Identity}
note over CGS: fabrikam_query<br>State: Not Approved<br><br>Pending:<br>Contosso<br><br>Accepted:<br>Litware
CGS--)FE: 
FE--)-Owner: <br>

User2->>+FE: Approve Query<br>{fabrikam_query}
FE->>CGS: Accept Document<br>{id: fabrikam_query}<br>{Contosso Identity}
note over CGS: fabrikam_query<br>State: Approved<br><br>Pending:<br><br><br>Accepted:<br>Litware, Contosso
CGS--)FE: 
FE--)-User2: <br>

```
