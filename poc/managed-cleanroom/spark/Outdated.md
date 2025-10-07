# Outdated content

## 1. Consortium formation
### 1.1. Option 1
**Members**: Ms Ads (`m1`), AP (`m2`), ISV (`m3`)  
**Users**: None

**Contract creation**: ISV proposes and only ISV vote required to Accept. No Ms Ads, AP member involvement.  
**Document creation**:  Only Ms Ads, AP member required to create/propose/vote. ISV persona could technically propose but not vote.

Contract contains additonal metadata for required voters: `.metadata.approval.requiredMembers: [m3]`.  

Document contains additonal metadata for required voters `.metadata.approval.requiredMembers: [m1, m2,...]` and a `.metadata.kind` property for applications to categorize the document type. 

CCF `resolve()` logic: `if requiredMembers.all(memberId => votes.contains(v => v.member_id == memberId) then Accept else Open`

Runtime consent revocation:

Sample document creation logic. Similar flow for contracts.
```powershell
# Create a new document under a contract.
$contractId="<AnExistingContractId>"
$documentId="1221"
$data = '{metadata: {"approval": {"requiredMembers": [m1, m2]}, "kind": "query"}, "hello": "world"}'
az cleanroom governance document create --data $data --id $documentId --contract-id $contractId

# Update an existing document (kind and body updated).
$version=(az cleanroom governance document show --id $documentId --query "version" --output tsv)
$data = '{metadata: {"approval": {"requiredMembers": [m1, m2]}, "kind": "datasource"}, "hello": "world", "foo": "bar"}'
az cleanroom governance document create --data $data --id $documentId --version $version

# Submitting a document proposal.
$version=(az cleanroom governance document show --id $documentId --query "version" --output tsv)
$proposalId=(az cleanroom governance document propose --version $version --id $documentId --query "proposalId" --output tsv)

# Vote on a document. As metadata.requireVoters is set so only the required members need to vote before the document gets accepted. Votes of members not mentioned in the requiredMembers list will be ignored if the cast a vote.
az cleanroom governance document vote --id $documentId --proposal-id $proposalId --action accept
```

### 1.2. Option 2
Members: ISV (`m3`)  
Users: Ms Ads (`u1`), AP (`u2`)

Actions:
Contract creation: ISV proposes and accepts. No Ms Ads/AP involvement.  
Document creation: Only Ms Ads, AP user required to create/accept.

Document requires no proposal or votes. Users accept/reject a document version and:
- If all required users have given acceptance then document is considered `accepted`.
- If atleast one user has accepted then its considered `proposed`.
- If atleast one user has rejected then its considered `rejected` even if others accepted.
- If no user has taken any action (or all reset their approval) then its considered `draft`.

```powershell
# Create a new document under a contract.
$contractId="<AnExistingContractId>"
$documentId="1221"
$data = '{metadata: {"approval": {"requiredUsers": [u1, u2], "kind": "query"}}, "hello": "world"}'
az cleanroom governance document create --data $data --id $documentId --contract-id $contractId

# Update an existing document.
$version=(az cleanroom governance document show --id $documentId --query "version" --output tsv)
$data = '{metadata: {"approval": {"requiredUsers": [u1, u2]}, "kind": "datasource"}, "hello": "world", "foo": "bar"}'
az cleanroom governance document create --data $data --id $documentId --version $version

# Approve/reject the version of the document. As metadata.requireUsers is set so only the required users need to approve before the document gets accepted. Votes of members not mentioned in the requiredMembers list will be ignored if the cast a vote.
$version=(az cleanroom governance document show --id $documentId --query "version" --output tsv)
az cleanroom governance document approval --id $documentId --version $version --action [accept|reject|reset]
```
Notes from discussion:
- Option 2 is the new way for documents. Remove the current `set_document` approach. Can have both requireMembers and requiredUsers for the documents.
- Option 1 is added only for contracts and not documents.
- If no required approvers is specified for option 1 or 2 then assume all current active members are required.
- Documents cannot be reset. Like contracts, once approved or rejected then its immutable. Can toggle execution consent just like contracts.
- update diagram: analytics frontend ==> ccf membership agent ==> [ccf membership service, ccf]
- update diagram: crud documents from frontend only, crud contracts from backend only
- If ms ads/AP don't want isv to manage their user/member certs then the analytics cli needs to hit CCF directly (see how to add that into the diagram)
- ccf membership service is like the ccf recovery service but at this point will have no break glass story if it fails to come up.

## CCF membership service
If the ISV manages the CCF identity for the users/members (like MS Ads, AP) then the same will be 
realized by having a `CCF membership service` that generates CCF identity certificates for the 
users/members and signs requests to CCF using the same. This service will be along the lines of the 
`CCF recovery service` which generates CCF recovery owner identity certificates.