# Clean Room Universe

## Core Infrastructure

### [TEE] Clean Room Application Sandbox

### [TEE] Clean Room Governance Service

### [TEE] Clean Room Spark Service

- Cluster
- Spark SQL Query Executor - Spark Application (Spark Pod Container)
- Spark SQL Query Executor - Client Library (Sidecar/SDK for CRUD of Query & Dataset descriptors.)
- Spark SQL Query Executor - Sample Client Application

## Microsoft.CleanRoom - Clean Room RP - Public

### [MOBO] Managed Clean Room Governance Service - Operator

## Microsoft.CleanRoom - Clean Room RP - Whitelisted

### [MOBO] Managed Clean Room Spark Service

### [MOBO] [TEE] Managed Clean Room Governance Service - Member

### [MOBO] [TEE] Managed Collaboration (Consortium)

- Create Collaboration
- Add User to Collaboration

### [MOBO] Managed Clean Room Application Sandbox

- Spark SQL Query Executor - Clean Room Application

## Clean Room Analytics

### Clean Room Analytics Frontend Service

### Clean Room Analytics UX

## Clean Room Inference

### Clean Room Inference Frontend Service

### Clean Room Inference UX

## Clean Room ML

### Clean Room ML Frontend Service

### Clean Room ML UX

OSS

RP - OSS Facilities [Zero Trust]

- Open Consortium - RP is only an operator
- Spark (if required) - RP is operator
- Generate Deployment Template

RP - Managed Clean Room Facilities [Verifiable Trust - TEE for sensitive operations, AME JIT Access for break glass operations but all such actions guaranteed to be captured in CCF ledger]

- Restricted Consortium - RP is operator as well as 2 members - "Confidential" and "Break glass", all collaborators are "users" (JWT). Confidential Member certificate generated and used within a TEE. Verifiable Trust by fetching CCF member cert details and invoking "data plane" API landing in a TEE to check if given certificates have been generated within said TEE.
- Predefined Contract and Clean Room Application for each Workload - well known measurement (versioned). Verifiable trust through source code and github attestation / CTS.
- Predefined "front end service" (stateless) for each Workload executing in a TEE - onboarded and whitelisted by APPID. Verifiable trust through source code and github attestation / CTS.
- Customer chooses workload as part of PUT, RP does all the work to setup consortium [TEE], deploy spark, propose & accept contract [TEE], generate deployment spec, deploy clean room and add customer as consortium user [TEE]
- RP maintains table of customer user context (JWT) to consortiums.
- RP returns front end service URL to customer.
- All resources deployed in customer tenant & subscription.

Front End Service

- Sign up flow - Invoke RP to create customer user context in RP tables (if not present)
- Sign in flow - Invoke RP to query consortiums available for customer to participate in
- Add Collaborator flow - Invoke RP to add another collaborator to consortium (only owner can add?). RP adds collaborators user context to table (if not present) and maps consortium to it.
- Collaboration flows - Invoke CGS directly from Front End Service with customer token.
- Execution flows - Invoke Clean Room directly from Front End Service with customer token.
