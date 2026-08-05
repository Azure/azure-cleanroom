# Inferencing Agent

The inferencing agent is the entry point for confidential model serving
in an AKS cleanroom. It exposes a single HTTPS endpoint (via an Envoy sidecar)
that handles both control plane and data plane operations:

- **Model deployment** (`/inferenceServices`) — Accepts model deployment
  requests, validates authorization, coordinates with the KServe inferencing
  frontend to create `InferenceService` resources, and tracks deployment status.
- **Inference routing** (`/ai/*`) — Authorizes each inference request against
  CGS, resolves the target predictor pod, and routes the request through Envoy's
  dynamic forward proxy over TLS.
- **OHTTP gateway** (`/ohttp-gateway`) — Provides a privacy-preserving
  inference path where request payloads are OHTTP-encapsulated (RFC 9458).
  These requests bypass the agent process entirely — Envoy routes them
  directly to the ohttp-gateway sidecar which decapsulates and forwards
  the plaintext to the predictor (over HTTPS).
- **Policy & governance** — Verifies consortium membership at startup, manages
  pod security policies, and logs audit events to CGS/CCF.

For details on the trust model, attestation, and TLS certificate issuance see
[AKS inferencing environment](/poc/managed-cleanroom/security.md#aks-inferencing-environment)
in the security FAQ.

The agent pod uses **ccr-proxy** (Envoy) as a sidecar to terminate incoming
HTTPS connections and route requests to the appropriate backend based on the URL
path. All external traffic enters through Envoy on port 443; internal backends
communicate over plain HTTP within the pod and over HTTPS for pod to pod.

## Pod Architecture

```
                          ┌─────────────────────────────────────────────────────┐
                          │             Inferencing Agent Pod                   │
                          │                                                     │
                          │  ┌──────────────────────────────────────────────┐   │
                          │  │  ccr-proxy (Envoy) :443                      │   │
  Client ── HTTPS ──────► │  │                                              │   │
                          │  │  TLS termination (CGS CA or local CA cert)   │   │
                          │  │                                              │   │
                          │  │  Routes:                                     │   │
                          │  │                                              │   │
                          │  │  /ai/* ──► ext_authz ──► dynamic_predictor ──┼───┼──► Predictor Pod :443
                          │  │                          (upstream TLS)      │   │      (HTTPS, CGS CA)
                          │  │                                              │   │           ▲
                          │  │  /ohttp-gateway ──► ohttp-gateway :8090 ─────┤   │           │
                          │  │                     (HTTP, in-pod)           │   │           │
                          │  │                                              │   │           │
                          │  │  /* ──► inferencing-agent :8080 ─────────────┤   │           │
                          │  │         (HTTP, in-pod)                       │   │           │
                          │  └──────────────────────────────────────────────┘   │           │
                          │                                                     │           │
                          │  ┌──────────────────┐  ┌─────────────────────────┐  │           │
                          │  │ inferencing-agent│  │ ohttp-gateway :8090 ────┼──┼───────────┘
                          │  │ :8080            │  │ (when ohttp.enabled)    │  │
                          │  └──────────────────┘  └─────────────────────────┘  │
                          │                                                     │
                          │  ┌─────────────────┐   ┌─────────────────────────┐  │
                          │  │ ccr-governance  │   │ skr                     │  │
                          │  │ :8300           │   │ :8284                   │  │
                          │  └─────────────────┘   └─────────────────────────┘  │
                          └─────────────────────────────────────────────────────┘
```

## Route Handling

### Control Plane — `/*` (catch-all)

All requests that do not match `/ai/` or `/ohttp-gateway` are routed to the
inferencing agent on `localhost:8080` over plain HTTP. This includes the
`/inferenceServices` control plane APIs for managing model deployments.

- **ext_authz**: disabled (the agent handles its own authentication).
- **TLS**: client → Envoy only (Envoy terminates HTTPS; backend is HTTP).

### OHTTP Gateway — `/ohttp-gateway`

When OHTTP is enabled, requests to `/ohttp-gateway` are routed to the
ohttp-gateway sidecar on `localhost:8090` over plain HTTP.

- **ext_authz**: enabled — Envoy calls `/authz/ohttp-gateway/{path}` which
  validates the caller via `ActiveUserChecker` (same as `/ai/*`) but does not
  extract model names or return routing headers.
- **TLS**: client → Envoy only.

### Data Plane — `/ai/*`

Inference requests (predict, infer, chat/completions, etc.) are sent under the
`/ai/` prefix. Envoy strips the `/ai/` prefix before forwarding to the predictor.

Supported KServe protocols under `/ai/`:

| Protocol           | Example path (after `/ai/` prefix) |
| ------------------ | ---------------------------------- |
| V1                 | `/ai/v1/models/{name}:predict`     |
| V2                 | `/ai/v2/models/{name}/infer`       |
| OpenAI (llama.cpp) | `/ai/v1/chat/completions`          |
| OpenAI (KServe)    | `/ai/openai/v1/chat/completions`   |

#### Authorization and Routing Flow

1. **ext_authz** — Envoy forwards the request to the agent's `/authz` endpoint
   with the `x-ms-cleanroom-authorization` header and up to 8 KB of the request
   body. The agent:
   - Validates the caller via `ActiveUserChecker`.
   - Extracts the model name from the URL path (V1/V2) or JSON body (OpenAI
     `"model"` field).
   - Returns `x-model-name` and `x-model-endpoint` response headers. The
     endpoint is the predictor service FQDN
     (`{model}-predictor-https.{namespace}.svc`).

2. **Lua filter** — Rewrites the `:authority` (Host) header to the value of
   `x-model-endpoint` and removes both `x-model-name` and `x-model-endpoint`
   from the upstream request.

3. **Dynamic forward proxy** — Resolves the predictor FQDN via DNS and routes
   the request to the correct predictor pod.

#### TLS

Data plane requests traverse two TLS hops:

```
Client ── HTTPS ──► Envoy :443 ── HTTPS ──► Predictor :443
           │                         │
           └─ Envoy server cert      └─ Upstream TLS: Envoy validates the
              (CGS CA or local CA)      predictor's cert against the CGS CA
                                        certificate (certs/upstream-ca.pem)
```

- **Client → Envoy**: Envoy presents a server certificate signed by either
  the CGS CA (production) or a locally generated CA (development). The
  bootstrap script generates this certificate at startup.
- **Envoy → Predictor**: Envoy initiates a new TLS connection to the predictor
  pod, validating its certificate against the CGS CA cert fetched from the
  governance sidecar (`GET localhost:8300/ca/info`).

### Timeout Handling

- **Data plane** (`/ai/*`): `timeout: 0s` (no timeout) to support SSE streaming
  for OpenAI chat/completions with `"stream": true`.
- **Control plane / OHTTP**: `timeout: 360s`.

## Example curl commands

The examples below assume:

- `$endpoint` is the inferencing agent HTTPS endpoint
  (e.g. `https://inferencing-<id>.centralindia.cloudapp.azure.com`).
- `$token` is a CGS access token obtained via
  `az cleanroom governance client get-access-token --query accessToken -o tsv --name <cgs-client>`.
- `$caCert` is the path to the CGS CA certificate file used to verify the
  agent's TLS certificate (e.g. `cleanroomca.crt`).

### Control Plane — Deploy a model (sklearn, KServe V2)

```pwsh
# Deploy an sklearn model using the KServe V2 protocol.
# Only name, modelId, and predictor.model.modelFormat are required.
curl -v --cacert $caCert `
  -X POST "$endpoint/inferenceServices" `
  -H "Content-Type: application/json" `
  -H "x-ms-cleanroom-authorization: Bearer $token" `
  -d '{
    "name": "hello-iris-1",
    "modelId": "<model-document-id>",
    "predictor": {
      "model": {
        "modelFormat": { "name": "sklearn" }
      }
    }
  }'
```

### Control Plane — Deploy a model (GGUF, OpenAI-compatible)

```pwsh
# Deploy a GGUF model (e.g. GPT-2) served via llama.cpp with the
# OpenAI-compatible chat/completions API.
curl -v --cacert $caCert `
  -X POST "$endpoint/inferenceServices" `
  -H "Content-Type: application/json" `
  -H "x-ms-cleanroom-authorization: Bearer $token" `
  -d '{
    "name": "hello-tinyllama-1",
    "modelId": "<model-document-id>",
    "predictor": {
      "model": {
        "modelFormat": { "name": "gguf" },
        "runtime": "llamacpp-server"
      }
    }
  }'
```

### Control Plane — Check deployment status

```pwsh
# Poll the deployment status until a URL is returned.
curl -v --cacert $caCert `
  "$endpoint/inferenceServices/hello-iris-1/status" `
  -H "x-ms-cleanroom-authorization: Bearer $token"
```

### Data Plane — KServe V2 inference (sklearn)

```pwsh
# Send an inference request for the iris model using the KServe V2 protocol.
# The /ai/ prefix is stripped by Envoy before forwarding to the predictor.
curl -v --cacert $caCert `
  -X POST "$endpoint/ai/v2/models/hello-iris-1/infer" `
  -H "Content-Type: application/json" `
  -H "x-ms-cleanroom-authorization: Bearer $token" `
  -d '{
    "inputs": [
      {
        "name": "input-0",
        "shape": [2, 4],
        "datatype": "FP32",
        "data": [
          [6.8, 2.8, 4.8, 1.4],
          [6.0, 3.4, 4.5, 1.6]
        ]
      }
    ]
  }'
```

### Data Plane — OpenAI chat/completions (llama.cpp)

```pwsh
# Send a chat completion request using the OpenAI-compatible protocol.
curl -v --cacert $caCert `
  -X POST "$endpoint/ai/v1/chat/completions" `
  -H "Content-Type: application/json" `
  -H "x-ms-cleanroom-authorization: Bearer $token" `
  -d '{
    "model": "hello-tinyllama-1",
    "messages": [
      { "role": "user", "content": "The capital of France is" }
    ],
    "max_tokens": 20
  }'
```

### Using Microsoft Agent Framework with Confidential Inferencing

For OpenAI-compatible endpoint (`/ai/v1/chat/completions`) models the inferencing agent's
endpoint can easily integrate with the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
for Python (or .NET). This lets you build AI agents that perform chat completions against
models deployed for confidential inferencing.

#### Example

```python
import asyncio

import httpx
import openai
from agent_framework import Agent
from agent_framework.openai import OpenAIChatCompletionClient

# --- Configuration ---
ENDPOINT = "https://inferencing-<id>.centralindia.cloudapp.azure.com/ai"
CA_CERT = "cleanroomca.crt"                   # path to CGS CA certificate
TOKEN = "<cgs-access-token>"                  # from `az cleanroom governance client get-access-token`
MODEL = "hello-tinyllama-1"                        # deployed model name


async def main():
    # Create an AsyncOpenAI client pointing at the inferencing agent's
    # OpenAI-compatible endpoint with the cleanroom auth header.
    async_client = openai.AsyncOpenAI(
        api_key="unused",
        base_url=f"{ENDPOINT}/v1",
        default_headers={
            "x-ms-cleanroom-authorization": f"Bearer {TOKEN}",
        },
        http_client=httpx.AsyncClient(verify=CA_CERT),
    )

    # Wrap in the Agent Framework's OpenAIChatCompletionClient.
    # Use OpenAIChatCompletionClient (Chat Completions API), not
    # OpenAIChatClient (Responses API), since llama.cpp exposes
    # the /v1/chat/completions endpoint.
    client = OpenAIChatCompletionClient(
        async_client=async_client,
        model=MODEL,
    )

    # Build an agent and run a query.
    agent = Agent(
        client=client,
        name="CleanroomAgent",
        instructions="You are a helpful assistant.",
    )

    result = await agent.run("What is the capital of France?")
    print(result)


if __name__ == "__main__":
    asyncio.run(main())
```

#### Notes

- **Authorization**: The cleanroom uses a custom
  `x-ms-cleanroom-authorization` header (not the standard `Authorization`
  header). Pass it via `default_headers` on the OpenAI client.
- **TLS**: Pass the CA certificate path to `httpx.AsyncClient(verify=...)`
  so the client trusts the cleanroom CA that signed the agent's server
  certificate.
- **Streaming**: The agent framework supports streaming responses. Use
  `async for chunk in agent.run("...", stream=True)` for streaming output.
- The automated test in
  [deploy-models.py](/test/onebox/model-serving/kserve-inferencing/deploy-models.py)
  (`--mode agent-framework`) exercises this exact pattern end-to-end.

### OHTTP Gateway — Fetch public key config

The OHTTP gateway exposes a well-known endpoint for clients to fetch the
server's HPKE public key configuration. This key is used by the client to
encapsulate (encrypt) the inference request.

```pwsh
# Fetch the OHTTP gateway's public key configuration.
curl -v --cacert $caCert "$endpoint/ohttp-gateway/.well-known/ohttp-keys"
```

The response has content type `application/ohttp-keys` and contains the binary
HPKE key configuration.

### OHTTP Gateway — Send an encapsulated inference request

OHTTP requests are binary (RFC 9458) and cannot be constructed with plain curl.
An OHTTP client library is needed to:

1. Fetch the gateway's public key config from
   `$endpoint/ohttp-gateway/.well-known/ohttp-keys`.
2. Encapsulate the inference request (e.g. the V2 or OpenAI payload) as a
   Binary HTTP message, then encrypt it using HPKE with the gateway's public key.
3. POST the encapsulated request to
   `$endpoint/ohttp-gateway/gateway/{model-name}` with content type
   `message/ohttp-req`.
4. Receive the `message/ohttp-res` response and decapsulate it to get the
   original inference response.

The [ohttp-client](/src/workloads/inferencing/ohttp/ohttp-client) container
implements this flow and can be used as a transparent proxy: the client sends
plain HTTP requests to the ohttp-client, which handles encapsulation/
decapsulation transparently.

```pwsh
# Using ohttp-client as a transparent proxy (assuming it is running
# on localhost:8070 and configured with the OHTTP gateway endpoint):
curl -v -X POST "http://localhost:8070/v1/chat/completions" `
  -H "Content-Type: application/json" `
  -d '{
    "model": "hello-tinyllama-1",
    "messages": [
      { "role": "user", "content": "The capital of France is" }
    ],
    "max_tokens": 20
  }'
```

## Files

| File                                                     | Description                                                                         |
| -------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| `src/proxy/https-http-inference-proxy/bootstrap.sh`      | Generates TLS certs, fetches CGS CA, templates the Envoy config, and launches Envoy |
| `src/proxy/https-http-inference-proxy/envoy-config.yaml` | Envoy config template with ext_authz, Lua, and dynamic forward proxy filters        |
| `Controllers/AuthzController.cs`                         | ext_authz endpoint — validates caller and returns model name/endpoint headers       |

## Design Decisions

### Why Envoy + ext_authz instead of a C# reverse proxy

An alternative approach would be to implement the `/ai/*` reverse proxy directly
in the inferencing-agent's C# code (e.g. using YARP or `HttpClient`-based
forwarding). We chose Envoy with ext_authz for the following reasons:

1. **Reuse of ccr-proxy infrastructure.** Every cleanroom pod already runs
   ccr-proxy (Envoy) as the TLS termination sidecar. The inference proxy mode
   extends this existing component rather than introducing a parallel HTTP
   pipeline in C#. The upstream TLS to predictor pods—certificate loading,
   CA validation, curve negotiation—is handled by Envoy's mature TLS stack
   with no additional code.

2. **Streaming without buffering.** Envoy natively streams HTTP request and
   response bodies byte-by-byte between client and upstream. A C# reverse proxy
   would need careful implementation to avoid buffering large inference payloads
   or SSE streams (OpenAI `"stream": true`) in memory, and would need to handle
   chunked transfer encoding, connection lifecycle, and backpressure correctly.

3. **Dynamic routing without connection management.** Envoy's dynamic forward
   proxy resolves predictor FQDNs via DNS at request time and manages connection
   pools, health checking, and retry logic. Replicating this in C# would require
   managing `HttpClient` instances per predictor, handling DNS refresh, connection
   pooling, and timeouts—all of which Envoy provides out of the box.

4. **Separation of concerns.** ext_authz keeps the agent focused on its domain
   logic (authorization, model name extraction) while Envoy handles transport
   concerns (TLS, routing, load balancing, observability). The agent returns two
   headers (`x-model-name`, `x-model-endpoint`) and Envoy does the rest. This
   avoids mixing networking plumbing into the business logic codebase.

5. **Observability.** Envoy emits structured access logs, upstream failure
   reasons, TLS stats, and connection metrics without any application code. A
   C# proxy would need explicit logging and metrics instrumentation for
   equivalent visibility into request routing and TLS failures.
