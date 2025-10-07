# Task Listing

## Managed Facilities

- CCF Network Manager
  - Pending work on Confidential Recovery Service
- Consortium Manager
  - Confidential Member Certificate Lifecycle
    - Certificate creation & idempotency
    - Certificate verification
    - Storage - SKR using AKV Premium
    - Storage - JIT Only through RBAC using AKV
    - Certificate Rollover/Update on break glass
    - Re-import on Service Code Update
    - Re-import on Infrastructure Update
    - Key Release Policy Calculation for supported service versions (forward compatible)
    - Audit Trail
  - Consortium API - RP
    - Create Consortium
      - CCF creation via CCF Network Manager
      - CCF verification
      - Propose-Accept/User_Identity
      - Propose-Accept/User_Certificate
    - Generate Contract
      - CCF verification
      - Clean Room Contract creation
      - Propose-Accept/Cleanroom_Contract
      - Clean Room k8s Deployment Template generation
      - Clean Room Key Release Policy generation
      - Propose-Accept/Cleanroom_Policy
      - Propose-Accept/Cleanroom_Deployment_Template
    - Add Collaborator (RP)
      - Propose-Accept/User_Email
      - Propose-Accept/User_Certificate
    - Recover Consortium
      - CCF verification
      - CCF confidential recovery protocol
      - CCF member recovery protocol
  - Consortium API - Workload Frontend
    - Add Collaborator (User)
      - Propose-Accept/User-Email
      - Propose-Accept/User_Certificate
    - Activate User
      - Email<->Identity resolution protocol
      - Propose-Accept/User_Identity
  - Operations API - Automation
    - Update Supported Versions
    - Get Supported Versions
  - Operations API - JIT
    - Break Glass
  - Deployment Model
    - Confidential Dataplane Service as part of RP
    - External API Endpoint - Load Balancer
    - Service Identity Propagation
    - Diagnostics
  - Security Model
    - Authorization - RP (In & Out)
    - Authorization - Workload Frontend (In)
    - Authorization - JIT Only (In)
    - No Auth Audit? - Attestation Check
    - Throttling
    - ?
  - Addendum
    - CCF Proposal Propose-Accept
      - User_Identity
      - User_Email
      - Cleanroom_Contract
      - Cleanroom_Policy
      - Cleanroom_Deployment_Template
- Membership Manager
  - Membership API - RP
    - Add Collaboration Mapping {identity, cgs-endpoint}
    - Add Collaboration Mapping {email, cgs-endpoint}
  - Membership API - Workload Frontend
    - List Collaborations {identity}
    - List Collaborations {email}
    - Add Collaboration Mapping {email, cgs-endpoint}
    - Update Collaboration Mapping {identity, email, cgs-endpoint}
- AKS Cluster Manager
  - Provision Namespace
  - Install Application {Helm Chart}
  - Get Application Status
- Clean Room Application Manager
  - Deploy Clean Room
- Spark Service Manager
  - Create Spark Service

## Developer Facilities

- Frontend Framework
  - collaborator management
  - clean room application invocation
  - governed document store access
- Clean Room Application Framework
  - governed document store access, confidential spark job submission, confidential spark verification
- Spark Application Framework
  - governed document store access, confidential access to sensitive data, confidential identity

## Workload - Spark Common

- Sidecar/SDK for R/W of "Query" document, "Dataset descriptor" document & relationship.

## Workload - Spark SQL Query Executor

- Frontend Service
- Clean Room Application
- Spark Application (Spark Pod Container)
