# Initial Setup Sequence Diagrams

Detailed sequence diagrams for interactions between various components as part of the initial setup.

## Create Clean Room

```mermaid
sequenceDiagram
title Create Clean Room

actor Owner as Litware

box brown Clean Room Services<br>AME
    participant RP as Clean Room<br>RP
    participant MM as Membership<br>Manager
    participant WF as Workflow Engine
    participant KM as AKS Cluster<br>Manager
    participant CCFM as CCF Network<br>Manager
end

box green Clean Room Services<br>AME+Confidential
    participant CM as Consortium<br>Manager
end

box purple Litware Subscription
    %% participant DNS as Private<br>DNS Zone
    participant AKS as AKS Cluster
end
box green Litware Subscription<br>Confidential
    participant Spark as Clean Room<br>Spark Service
    participant CGS as Clean Room<br>Governance
end

    Owner->>RP: Create Clean Room

    RP->>+WF: Start Workflow

    WF->>+KM: Create<br>AKS Cluster
    note over KM: Create VNet & subnets for AKS
    KM->>AKS: Create Cluster
    KM->>AKS: Update AKS MI permissions
    note over KM: Setup workload identity<br>for External DNS

    loop VN2, External DNS, Spark Operator
        KM->>AKS: Install Helm Chart
        AKS--)KM: <br>
    end
    KM--)-WF: k8s Endpoint

    WF->>+CM: Get Consortium member certificate
    Note over CM: Create member certificate
    Note over CM: Store member certificate<br>with SKR<br>(AKV/mHSM/KMS)
    CM--)-WF: Member certificate details

    WF->>+CCFM: Create CCF network<br>as sole member
    CCFM->>CGS: Deploy
    CCFM--)-WF: CCF Endpoint

    WF->>+CM: Prepare Consortium<br>{CCF Endoint}
    Note over CM: Verify consortium
    CM->>CGS: Accept membership
    CM->>CGS: Propose User_Identity<br>{Litware Identity}
    CM->>CGS: Accept User_Identity<br>{Litware Identity}
    note over CGS: Litware<br>Active
    CGS--)CM: 
    CM--)-WF: Consortium prepared

    WF->>+MM: Add Mapping<br>{Litware Identity,<br>Clean Room ID}
    note over MM: Update<br>Global DB
    MM--)-WF: 

    WF--)-RP: Workflow Complete
    RP--)Owner: <br>
```

## Enable Workload

```mermaid
sequenceDiagram
title Enable Workload

actor Owner as Litware

box brown Clean Room Services<br>AME
    participant RP as Clean Room<br>RP
    participant WF as Workflow Engine
    participant SM as Spark Service<br>Manager
    participant CRM as Clean Room<br>Application<br>Manager
    participant KM as AKS Cluster<br>Manager
end

box green Clean Room Services<br>AME+Confidential
    participant CM as Consortium<br>Manager
end

box purple Litware Subscription
    participant DNS as Private<br>DNS Zone
    participant AKS as AKS Cluster
end
box green Litware Subscription<br>Confidential
    participant Spark as Clean Room<br>Spark Service
    participant CGS as Clean Room<br>Governance
    participant TEE as Clean Room<br>Application
end

    Owner->>RP: Enable Workload

    RP->>+WF: Start Workflow

    WF->>+SM: Create Spark Service

    SM->>+KM: Provision Service Namespace
    KM->>+DNS: Create private dns zone<br>{sparkservice.svc}
    KM->>+DNS: Create private dns link with vnet
    KM->>AKS: Create namespace {sparkservice}
    KM--)-SM: 

    SM->>+KM: Install<br>{Spark Service Helm Chart}
    KM->>AKS: Install Helm Chart
    AKS->>Spark: Deploy
    AKS--)KM: 
    KM--)-SM: 

    SM->>+KM: Provision Workload Namespace
    KM->>+DNS: Create private dns zone<br>{workload.svc}
    KM->>+DNS: Create private dns link with vnet
    KM->>AKS: Create namespace {workload}
    KM--)-SM: 

    SM--)-WF: Spark Endpoint



    WF->>+CM: Generate Contract<br>{Consortium Endpoint, Spark Endpoint}
    CM->>CGS: Get Attestation Report
    Note over CM: Verify consortium
    CM->>CGS: Propose Workload Contract
    CM->>CGS: Accept Workload Contract
    Note over CM: Generate security policy
    CM->>CGS: Propose cleanroom_policy
    CM->>CGS: Accept cleanroom_policy
    CM->>CGS: Propose Deployment Template<br>[k8s - Clean Room Helm Chart]
    CM->>CGS: Accept Deployment Template<br>[k8s - Clean Room Helm Chart]
    CM--)-WF: Key Release Policy

    WF->>+CRM: Deploy clean room
    CRM->>+KM: Provision Workload Namespace
    KM->>+DNS: Create private dns zone<br>{workload.svc}
    KM->>+DNS: Create private dns link with vnet
    KM->>AKS: Create namespace {workload}
    KM--)-CRM: 

    CRM->>CGS: Get Deployment Template<br>[k8s - Clean Room Helm Chart]
    CRM->>+KM: Install<br>{Clean Room Helm Chart}
    KM->>AKS: Install Helm Chart
    AKS->>TEE: Deploy
    AKS--)KM: 
    KM--)-CRM: 

    CRM--)-WF: Clean room endpoint

    WF--)-RP: Workflow Complete
    RP--)Owner: Workload Frontend Endpoint
```

## View Collaboration (Owner)

```mermaid
sequenceDiagram
title View Collaboration

actor Owner as Litware

box green Clean Room Services<br>AME+Confidential
    participant FE as Workload<br>Frontend<br>(Dataplane API)
end

box brown Clean Room Services<br>AME
    participant RP as Clean Room<br>RP
    participant MM as Membership<br>Manager
end

box green Litware Subscription<br>Confidential
    participant CGS as Clean Room<br>Governance
end

    Owner->>+FE: List Collaborations
    FE->>+MM: List Collaborations<br>{Litware Identity}
    MM--)-FE: Collaborations[]
    FE->>+MM: List Collaborations<br>{litware@live.com}
    MM--)-FE: <empty>
    FE--)-Owner: Collaborations List

    Owner->>+FE: View Collaboration
    FE->>CGS: Get Collaboration Details<br>{Litware Identity}
    CGS--)FE: <br>
    FE--)-Owner: Collaboration View

```

## Add Collaborator (Workload Frontend) - Zero Trust

```mermaid
sequenceDiagram
title Add Collaborator

actor User as Fabrikam
actor Owner as Litware

box green Clean Room Services<br>AME+Confidential
    participant FE as Workload<br>Frontend<br>(Dataplane API)
end

box brown Clean Room Services<br>AME
    participant RP as Clean Room<br>RP
    participant MM as Membership<br>Manager
end

box green Clean Room Services<br>AME+Confidential
    participant CM as Consortium<br>Manager
end

box green Litware Subscription<br>Confidential
    participant CGS as Clean Room<br>Governance
end

    Owner->>+FE: Add Collaborator<br>{fabrikam@live.com}
    FE->>+CM: Add Collaborator<br>{fabrikam@live.com,<br>Litware Identity}
    CM->>CGS: Propose User_Email{<br>add:fabrikam@live.com,<br>by:Litware Identity}
    CM->>CGS: Accept User_Email<br>{fabrikam@live.com}
    note over CGS: Fabrikam<br>Pending
    CGS--)CM: 
    CM--)-FE: 

    FE->>+MM: Add Mapping<br>{fabrikam@live.com, Clean Room ID}
    note over MM: Update<br>Global DB
    MM--)-FE: 
    FE--)-Owner: 
```

## Add Collaborator (Clean Room RP) - AME Trust

```mermaid
sequenceDiagram
title Add Collaborator

actor User as Fabrikam
actor Owner as Litware

box green Clean Room Services<br>AME+Confidential
    participant FE as Workload<br>Frontend<br>(Dataplane API)
end

box brown Clean Room Services<br>AME
    participant RP as Clean Room<br>RP
    participant MM as Membership<br>Manager
    participant WF as Workflow Engine
end

box green Clean Room Services<br>AME+Confidential
    participant CM as Consortium<br>Manager
end

box green Litware Subscription<br>Confidential
    participant CGS as Clean Room<br>Governance
end

    Owner->>RP: Add Collaborator<br>{fabrikam@live.com}
    RP->>+WF: Start Workflow

    WF->>+CM: Add Collaborator<br>{fabrikam@live.com}
    CM->>CGS: Propose User_Email{<br>add:fabrikam@live.com,<br>by:RP Identity}
    CM->>CGS: Accept User_Email<br>{fabrikam@live.com}
    note over CGS: Fabrikam<br>Pending
    CGS--)CM: 
    CM--)WF: 

    WF->>+MM: Add Mapping<br>{fabrikam@live.com,<br>Clean Room ID}
    note over MM: Update<br>Global DB
    MM--)-WF: 
    WF--)-RP: 
    RP--)Owner: 
```

## View Collaboration (User)

```mermaid
sequenceDiagram
title View Collaboration

actor Owner as Litware
actor User as Fabrikam

box green Clean Room Services<br>AME+Confidential
    participant FE as Workload<br>Frontend<br>(Dataplane API)
end

box brown Clean Room Services<br>AME
    participant RP as Clean Room<br>RP
    participant MM as Membership<br>Manager
end

box green Clean Room Services<br>AME+Confidential
    participant CM as Consortium<br>Manager
end

box green Litware Subscription<br>Confidential
    participant CGS as Clean Room<br>Governance
end

    User->>+FE: List Collaborations
    FE->>+MM: List Collaborations<br>{Fabrikam Identity}
    MM--)-FE: <empty>
    FE->>+MM: List Collaborations<br>{fabrikam@live.com}
    MM--)-FE: Collaborations[]
    FE--)-User: Collaborations List

    User->>+FE: View Collaboration
    FE->>CGS: Get Collaboration Details<br>{Fabrikam Identity}
    note over CGS: Fabrikam<br>Pending
    CGS--)FE: Invitation Pending
    FE--)-User: Invitation Pending

    User->>+FE: Accept Invitation
    FE->>+CGS: Accept Invitation<br>{Fabrikam Identity}
    note over CGS: Update<br>{Fabrikam Identity}
    note over CGS: Fabrikam<br>Inactive
    CGS--)-FE: 

    FE->>+CM: Activate User<br>{fabrikam@live.com}
    CM->>CGS: Get Invitation<br>{fabrikam@live.com}
    CGS--)CM: {Fabrikam Identity}
    CM->>CGS: Propose User_Identity<br>{Fabrikam Identity}
    CM->>CGS: Accept User_Identity<br>{Fabrikam Identity}
    note over CGS: Fabrikam<br>Active
    CGS--)CM: 
    CM--)-FE: 

    FE->>+MM: Update Mapping<br>{fabrikam@live.com,<br>Fabrikam Identity,<br>Clean Room ID}
    note over MM: Transactional<br>Update<br>Global DB
    MM--)-FE: 
    FE--)-User: Invitation Accepted

    User->>+FE: View Collaboration
    FE->>CGS: Get Collaboration Details<br>{Fabrikam Identity}
    CGS--)FE: <br>
    FE--)-User: Collaboration View

```
