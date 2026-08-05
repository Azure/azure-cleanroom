# Inferencing Deployment Diagnosability Proposal

## Problem Statement

When an inferencing predictor pod (e.g. `tinyllama-2-predictor-...`) fails, the failure cascade is:

1. **Identity sidecar** — fails to acquire token (e.g. AADSTS700213 subject mismatch)
2. **Blobfuse launcher** — can't mount storage (identity token unavailable)
3. **kserve-container** — CrashLoopBackOff (model files not found on empty mount)

Currently, diagnosing this requires manual `kubectl describe pod` + `kubectl logs` on each container. None of the layers above surface this information.

### Current Status Reporting Gaps

| Layer | What it reports | What's missing |
|---|---|---|
| **InferenceService CR** `.status` | `Ready=False`, no URL | No pod-level detail, no sidecar error |
| **Frontend** `/inferencing/status/{name}` | Raw InferenceService status dict | No container crash info, no volume error |
| **Agent** `/inferenceServices/{name}/status` | Proxies frontend response | Same gap |
| **MDI controller** `DeploymentStatus.IsReady()` | "not ready, requeueing" forever | No timeout, no failure surfacing |
| **Blobfuse launcher** | Writes `.volume.error` to shared emptyDir | Not exposed externally; readiness probe returns 200 even on error |
| **Identity sidecar** | Logs AADSTS error | Not exposed externally |

## Proposed Solution: K8s-Native Error Surfacing

Use native Kubernetes mechanisms to surface container errors into `pod.status` — no sidecar-to-sidecar HTTP calls needed.

### K8s-Native Mechanisms

1. **`terminationMessagePath` + `terminationMessagePolicy`** — K8s captures structured error from `/dev/termination-log` (or last log lines) into `pod.status.containerStatuses[].lastState.terminated.message`
2. **Readiness probes** — failed probes set `containerStatuses[].ready = false` and emit `Warning` Events queryable via K8s API
3. **Pod conditions** — `ContainersReady` and `Ready` updated automatically from probe results

### Per-Sidecar Changes

| Sidecar | Current Behavior | Proposed Change |
|---|---|---|
| **blobfuse-launcher** | Stays running on failure; writes `.volume.error`; readiness returns 200 for both success AND error | On unrecoverable error: write structured JSON to `/dev/termination-log` and exit non-zero. K8s surfaces this in `lastState.terminated.message`. |
| **identity-sidecar** | Stays running, logs AADSTS errors | Add readiness probe (`GET /ready` returning 503 after N consecutive token failures). K8s marks `ready=false` and emits Events with probe failure message. |
| **kserve-container** | Crashes with FileNotFoundError | Set `terminationMessagePolicy: FallbackToLogsOnError` on container spec. K8s captures Python traceback in `lastState.terminated.message`. |

### Blobfuse Design Decision: Exit vs Stay Running

**Option A (Recommended): Exit on unrecoverable error**
- Write error JSON to `/dev/termination-log`, exit non-zero
- K8s restarts it (CrashLoopBackOff), `lastState.terminated.message` has the error
- Both containers' failure reasons visible in pod status

**Option B: Readiness probe reflects failure state**
- Change readiness handler: return 503 (not 200) when `.volume.error` exists
- K8s marks `ready=false`, emits `Unhealthy` event
- Container stays running for potential retry

Option A is more K8s-idiomatic — if a sidecar can't do its job, it should fail clearly.

### What the Frontend Reads (No Sidecar HTTP Calls)

All from the K8s Pod API via `kubernetes.client.CoreV1Api`:

```python
pod.status.container_statuses[].ready              # False if probe fails
pod.status.container_statuses[].restart_count      # >0 = crashing
pod.status.container_statuses[].state              # waiting/running/terminated
pod.status.container_statuses[].last_state.terminated.message  # THE ERROR
pod.status.container_statuses[].last_state.terminated.exit_code
```

## Frontend `/inferencing/status/{model_name}` Enhancement

Enrich the existing response with pod diagnostics when the InferenceService is not ready:

```python
@app.get("/inferencing/status/{model_name}")
async def get_status(model_name: str):
    inference_svc = k8s_client.get_inference_service(...)
    job_status = inference_svc.status

    # If not ready, augment with pod diagnostics from K8s API
    pod_health = None
    if not _is_ready(job_status):
        pod_health = k8s_client.get_pod_health(model_name, namespace)

    return {
        "id": model_name,
        "status": job_status,
        "podHealth": pod_health,  # None when healthy
    }
```

### Example `podHealth` Response

```json
{
  "pods": [
    {
      "name": "tinyllama-2-predictor-68f8c4d686-hbls7",
      "phase": "Running",
      "containers": [
        {
          "name": "kserve-container",
          "ready": false,
          "state": "CrashLoopBackOff",
          "restartCount": 5,
          "lastTermination": {
            "exitCode": 1,
            "message": "FileNotFoundError: model file not found at /mnt/remote/..."
          }
        },
        {
          "name": "blobfuse-launcher",
          "ready": false,
          "restartCount": 3,
          "lastTermination": {
            "exitCode": 1,
            "message": "{\"error\":\"mount_failed\",\"cause\":\"identity token acquisition failed: AADSTS700213\"}"
          }
        },
        {
          "name": "identity-sidecar",
          "ready": false,
          "state": "Running"
        }
      ]
    }
  ]
}
```

### Frontend `get_pod_health` Implementation

```python
def get_pod_health(self, model_name: str, namespace: str) -> dict:
    core_api = kubernetes.client.CoreV1Api()
    pods = core_api.list_namespaced_pod(
        namespace=namespace,
        label_selector=f"serving.kserve.io/inferenceservice={model_name}",
    )
    result = {"pods": []}
    for pod in pods.items:
        pod_info = {
            "name": pod.metadata.name,
            "phase": pod.status.phase,
            "containers": [],
        }
        for cs in pod.status.container_statuses or []:
            container_info = {
                "name": cs.name,
                "ready": cs.ready,
                "restartCount": cs.restart_count,
            }
            if cs.state.waiting:
                container_info["state"] = cs.state.waiting.reason
                container_info["message"] = cs.state.waiting.message
            elif cs.state.terminated:
                container_info["state"] = "Terminated"
                container_info["lastTermination"] = {
                    "exitCode": cs.state.terminated.exit_code,
                    "reason": cs.state.terminated.reason,
                    "message": cs.state.terminated.message,
                }
            else:
                container_info["state"] = "Running"
            if cs.last_state and cs.last_state.terminated:
                container_info["lastTermination"] = {
                    "exitCode": cs.last_state.terminated.exit_code,
                    "reason": cs.last_state.terminated.reason,
                    "message": cs.last_state.terminated.message,
                }
            pod_info["containers"].append(container_info)
        result["pods"].append(pod_info)
    return result
```

## MDI Controller Improvements

In `modeldeployment_controller.go`:

1. **Parse pod health from status response** — if `podHealth` is included, extract container crash info
2. **Add deployment timeout** — if not ready after N minutes (configurable, e.g. 10min), transition to `Failed` with diagnostic message rather than requeueing forever
3. **Surface pod errors in MDI status conditions** — e.g., `ConditionType: DeploymentHealthy`, `Message: "Container blobfuse-launcher terminated: mount_failed: AADSTS700213"`

## Implementation Order

1. **Frontend `/status` enrichment** — highest ROI, minimal code, backward-compatible (`podHealth` is additive)
2. **`KubernetesClient.get_pod_health()`** — the plumbing that `/status` calls
3. **Blobfuse: exit on unrecoverable error** + write to `/dev/termination-log`
4. **kserve-container: `terminationMessagePolicy: FallbackToLogsOnError`** in pod spec
5. **Identity sidecar: readiness probe** reflecting token acquisition health
6. **MDI controller: timeout + error surfacing** — stops infinite requeue loop

## Files to Modify

| File | Change |
|---|---|
| `src/workloads/inferencing/kserve-inferencing-frontend/.../clients/kubernetes_client.py` | Add `get_pod_health()` method |
| `src/workloads/inferencing/kserve-inferencing-frontend/.../main.py` | Enrich `/status` endpoint |
| `src/blobfuse-launcher/src/blobfuse_launcher/main.py` | Exit non-zero + write termination message on unrecoverable failure |
| `src/internal/frontend-internal/.../cleanroom_application_builder.py` | Add `terminationMessagePolicy` to kserve-container spec |
| `src/identity/` | Add readiness probe endpoint |
| `src/cleanroom-operator/internal/controller/modeldeployment_controller.go` | Timeout + parse pod health |
| `src/cleanroom-operator/internal/client/inferencing_client.go` | Parse `podHealth` from status response |
