# ccr-governance<!-- omit from toc -->
- [HTTP API](#http-api)
  - [Events](#events)
  - [Secrets](#secrets)
  - [Consent checks](#consent-checks)
  - [OAuth Token (OIDC Issuer)](#oauth-token-oidc-issuer)
- [Error response](#error-response)
- [Configuration](#configuration)

The ```ccr-governance``` container instantiates a web server (<http://localhost:port>) which exposes a REST API so that other containers can interact with the CGS service to check for consent, retrieve secrets, request tokens and insert audit events as part of the clean room execution. It internally fetches the attestation report and generates an RSA key pair during its launch. These keys are used as signing/encryption keys to interact with the CGS service when invoking the CGS APIs.

## HTTP API
### Events
The PUT `/events` PUT method supports following query parameters and a JSON body with event payload. The query parameters are:
| Name | | Required | Description |
| --- | --- | --- | --- |
| `id` | | No | The identifier under which these events are getting logged. Eg the contractId of the contract. |
| `scope` | | No | The logical group/scope of the events. Eg scope value can be "contracts" to go along with the id of the contract specifed. |

Eg: PUT `localhost:port/events?id=1234&scope=contracts`

The JSON body of the following format is also required:

```json
{
  "key1":"value1",
  "key2":{
    "key3":{
      "key4":"value4"
    }
  },
  "...":"..."
}
```
Eg. it could be as simple as:
```json
{
  "message": "<Some message of relevance to the clean room>"
}
```
The structure of the JSON document is opaque to both the ccr-governance container and CGS services. The JSON content is saved as-is by the CGS service and returned via the get event APIs.

### Secrets
The `/secrets/{secretId}` POST method expects no request body and responds with a JSON of the following format:

```json
{
  "value": "<secretvalue>"
}
```

### Consent checks
The `/consentcheck/[logging|telemetry|execution]` POST method expects no request body and responds with a JSON of the following format:

```json
{
  "status": "[enabled|disabled]",
  "reason" : {
    "code": "code",
    "message": "message"
  }
}
```

The HTTP response status code of `200` indicates that the API executed successfully and the result of the consent check for clean room execution is returned in the `status` property.
- `status: enabled` means consent check passed.
- `status: disabled` means consent was not given with `reason` populated with the reason for failure.  

A HTTP `4xx/5xx` response status code indicates the API execution itself failed and hence unable to check for consent. This could be due to network issues or service side failures.

### OAuth Token (OIDC Issuer)
The `/oauth/token` POST method returns an ID token (also referred to as a client assertion) that can be used for getting an Azure access token via [federated identity credentials](https://learn.microsoft.com/en-us/entra/workload-id/workload-identity-federation-create-trust-user-assigned-managed-identity?pivots=identity-wif-mi-methods-azp#other) with CGS acting as the OIDC Issuer/IdP. The method expects the following query parameters:
| Name | | Required | Description |
| --- | --- | --- | --- |
| `sub` | | Yes | The subject identifier value to set as the `sub` claim in the token. |
| `tenantId` | | Yes | The tenant ID for the Azure tenant to which the returned token will be presented. |
| `aud` | | Yes | The audience value to set as the `aud` claim in the token. |

The response is JSON of the following format:

```json
{
  "value": "<token>"
}
```
With this token the client can [exchange it with an access token](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow#third-case-access-token-request-with-a-federated-credential) from Azure by performing the following steps:
```pwsh
$clientId="<user assigned managed identity client Id>"
$tenantId="<Azure AD tenant Id>"
$scope="<eg: https://vault.azure.net/.default>"
$sub="<subject identifier value configured in federated credentials>"
$aud="api://AzureADTokenExchange"
$sidecarPort="<port on which ccr-governance sidecar is listening>"

# Get the ID token via the sidecar.
$clientAssertion=$(curl -sS -X POST "http://localhost:${sidecarPort}/oauth/token?&sub=${sub}&tenantId=${tenantId}&aud=${aud}") | jq -r '.value';

# Exchange it for an access token from Azure.
$endpoint = "https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/token";
$client_assertion_type = "urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer";
$scope = [uri]::EscapeDataString($scope);
$data = "scope=${scope}&client_id=${clientId}&client_assertion_type=${client_assertion_type}&client_assertion=${clientAssertion}&grant_type=client_credentials";
$accessToken=(curl -sS -X POST -H "content-type: application/x-www-form-urlencoded" -d $data ${endpoint})
$accessToken | jq
```
---
## Error response
Upon error, these endpoints return a payload of the following format:

```json
{
  "error": {
    "code": "errorCode",
    "message": "error message"
  }
}
```

## Configuration
The following environment variables control the configuration of this sidecar:
| Name | Type | Required | Description |
| --- | --- | --- | --- |
| `ccrgovEndpoint` | `string` | Yes | The endpoint address of the CGS service instance. Eg: https://contoso.westus.azurecontainer.io |
| `ccrgovApiPathPrefix` | `string` | Yes | The path prefix to use along with the endpoint address to form the complete base path of `<ccrgovEndpoint>/<ccrgovApiPathPrefix>` for invoking the CGS APIs. Eg: value of `app/contracts/dea12ad2` will result in the base path of `https://contoso.westus.azurecontainer.io/app/contracts/dea12ad2`.|
| `serviceCertPath` | `string` | Yes | The path to the file containing the PEM-encoded certificate to use for SSL connection verification when connecting to the `ccrgovEndpoint`.|
| `serviceCert` | `string` | Yes | The base64 representation of the PEM-encoded certificate to use for SSL connection verification when connecting to the `ccrgovEndpoint`.|

Only one of `serviceCert` or `serviceCertPath` need to be specified, not both.
