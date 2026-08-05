package kubeletproxy.admission

import rego.v1

# Generic policy verification engine for kubelet-proxy pod admission.
#
# Input:  the full pod object merged with policy data:
#   input.spec.containers, input.spec.initContainers — pod containers
#   input.policy — array of container policy objects:
#   [
#     {
#       "name": "<container-name>",
#       "properties": {
#         "image": "<required-image>",
#         "command": ["cmd", ...],
#         "args": ["arg1", ...],
#         "environmentVariables": [{"name": "X", "value": "Y", "regex": false}],
#         "volumeMounts": [{"name": "vol", "mountPath": "/mnt", "readOnly": true}],
#         "privileged": false,
#       }
#     }
#   ]
#
# Produces:
#   allow  — true if the pod matches the policy, false otherwise
#   reasons — set of human-readable denial reason strings

default allow := false

# Allow if there are no deny reasons.
allow if {
    count(deny) == 0
}

# Build a lookup from container name to its policy properties.
policy_by_name[name] := props if {
    some entry in input.policy
    name := entry.name
    props := entry.properties
}

# --- Container existence checks ---

# Every container in the pod must have a corresponding policy entry.
deny contains msg if {
    some container in input.spec.containers
    not policy_by_name[container.name]
    msg := sprintf("containers '%s' not found in policy", [container.name])
}

# Every init container in the pod must have a corresponding policy entry.
deny contains msg if {
    some container in input.spec.initContainers
    not policy_by_name[container.name]
    msg := sprintf("initContainers '%s' not found in policy", [container.name])
}

# --- Image checks ---

deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    props.image != ""
    container.image != props.image
    msg := sprintf(
        "containers '%s': image '%s' does not match policy image '%s'",
        [container.name, container.image, props.image],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    props.image != ""
    container.image != props.image
    msg := sprintf(
        "initContainers '%s': image '%s' does not match policy image '%s'",
        [container.name, container.image, props.image],
    )
}

# --- Command checks ---

# Pod command must match policy command.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    count(props.command) > 0
    not commands_equal(container, props)
    msg := sprintf(
        "containers '%s': command %v does not match policy command %v",
        [container.name, container.command, props.command],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    count(props.command) > 0
    not commands_equal(container, props)
    msg := sprintf(
        "initContainers '%s': command %v does not match policy command %v",
        [container.name, container.command, props.command],
    )
}

# Pod has command but policy does not specify one.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    count(object.get(props, "command", [])) == 0
    count(object.get(container, "command", [])) > 0
    msg := sprintf(
        "containers '%s': pod has command %v but policy does not allow command",
        [container.name, container.command],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    count(object.get(props, "command", [])) == 0
    count(object.get(container, "command", [])) > 0
    msg := sprintf(
        "initContainers '%s': pod has command %v but policy does not allow command",
        [container.name, container.command],
    )
}

commands_equal(container, props) if {
    count(container.command) == count(props.command)
    every i, cmd in container.command {
        cmd == props.command[i]
    }
}

# --- Args checks ---

# Pod args must match policy args.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    count(props.args) > 0
    not args_equal(container, props)
    msg := sprintf(
        "containers '%s': args %v does not match policy args %v",
        [container.name, container.args, props.args],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    count(props.args) > 0
    not args_equal(container, props)
    msg := sprintf(
        "initContainers '%s': args %v does not match policy args %v",
        [container.name, container.args, props.args],
    )
}

# Pod has args but policy does not specify any.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    count(object.get(props, "args", [])) == 0
    count(object.get(container, "args", [])) > 0
    msg := sprintf(
        "containers '%s': pod has args %v but policy does not allow args",
        [container.name, container.args],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    count(object.get(props, "args", [])) == 0
    count(object.get(container, "args", [])) > 0
    msg := sprintf(
        "initContainers '%s': pod has args %v but policy does not allow args",
        [container.name, container.args],
    )
}

args_equal(container, props) if {
    count(container.args) == count(props.args)
    every i, arg in container.args {
        arg == props.args[i]
    }
}

# --- Environment variable checks ---

# Policy env var value must match pod env var value.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    some policy_env in props.environmentVariables
    actual_val := env_value(container, policy_env.name)
    not env_matches(actual_val, policy_env)
    msg := sprintf(
        "containers '%s': env var '%s' value '%s' does not match policy value '%s'",
        [container.name, policy_env.name, actual_val, policy_env.value],
    )
}

# Policy env var must exist in pod.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    some policy_env in props.environmentVariables
    not has_env(container, policy_env.name)
    msg := sprintf(
        "containers '%s': env var '%s' required by policy but not found in pod",
        [container.name, policy_env.name],
    )
}

# Pod env var must exist in policy.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    count(object.get(props, "environmentVariables", [])) > 0
    some env in container.env
    not policy_has_env(props, env.name)
    msg := sprintf(
        "containers '%s': env var '%s' found in pod but not in policy",
        [container.name, env.name],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    some policy_env in props.environmentVariables
    actual_val := env_value(container, policy_env.name)
    not env_matches(actual_val, policy_env)
    msg := sprintf(
        "initContainers '%s': env var '%s' value '%s' does not match policy value '%s'",
        [container.name, policy_env.name, actual_val, policy_env.value],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    some policy_env in props.environmentVariables
    not has_env(container, policy_env.name)
    msg := sprintf(
        "initContainers '%s': env var '%s' required by policy but not found in pod",
        [container.name, policy_env.name],
    )
}

# Pod env var must exist in policy (initContainers).
deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    count(object.get(props, "environmentVariables", [])) > 0
    some env in container.env
    not policy_has_env(props, env.name)
    msg := sprintf(
        "initContainers '%s': env var '%s' found in pod but not in policy",
        [container.name, env.name],
    )
}

# Pod has env vars but policy does not specify any.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    count(object.get(props, "environmentVariables", [])) == 0
    count(object.get(container, "env", [])) > 0
    msg := sprintf(
        "containers '%s': pod has env vars but policy does not allow any",
        [container.name],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    count(object.get(props, "environmentVariables", [])) == 0
    count(object.get(container, "env", [])) > 0
    msg := sprintf(
        "initContainers '%s': pod has env vars but policy does not allow any",
        [container.name],
    )
}

# Extract value of an env var by name.
env_value(container, name) := value if {
    some env in container.env
    env.name == name
    value := env.value
}

# Check if a container has an env var with the given name.
has_env(container, name) if {
    some env in container.env
    env.name == name
}

# Check if an actual value matches a policy env var (supports regex).
env_matches(actual_val, policy_env) if {
    policy_env.regex == true
    regex.match(policy_env.value, actual_val)
}

env_matches(actual_val, policy_env) if {
    not policy_env.regex
    actual_val == policy_env.value
}

# Check if a policy has an env var with the given name.
policy_has_env(props, name) if {
    some policy_env in props.environmentVariables
    policy_env.name == name
}

# --- Volume mount checks ---

# Policy mount must match pod mount path.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    some policy_mount in props.volumeMounts
    pod_mount := volume_mount_by_name(container, policy_mount.name)
    pod_mount.mountPath != policy_mount.mountPath
    msg := sprintf(
        "containers '%s': volume mount '%s' mountPath '%s' does not match policy '%s'",
        [container.name, policy_mount.name, pod_mount.mountPath, policy_mount.mountPath],
    )
}

# Policy mount must match pod mount readOnly.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    some policy_mount in props.volumeMounts
    pod_mount := volume_mount_by_name(container, policy_mount.name)
    pod_mount.readOnly != policy_mount.readOnly
    msg := sprintf(
        "containers '%s': volume mount '%s' readOnly=%v does not match policy readOnly=%v",
        [container.name, policy_mount.name, pod_mount.readOnly, policy_mount.readOnly],
    )
}

# Policy mount must exist in pod.
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    some policy_mount in props.volumeMounts
    not has_volume_mount(container, policy_mount.name)
    msg := sprintf(
        "containers '%s': volume mount '%s' required by policy but not found in pod",
        [container.name, policy_mount.name],
    )
}

# Pod mount must exist in policy (skip Kubernetes auto-injected mounts).
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    count(object.get(props, "volumeMounts", [])) > 0
    some mount in container.volumeMounts
    not is_k8s_injected_mount(mount)
    not policy_has_volume_mount(props, mount.name)
    msg := sprintf(
        "containers '%s': volume mount '%s' found in pod but not in policy",
        [container.name, mount.name],
    )
}

# Pod has volume mounts but policy does not specify any (skip Kubernetes auto-injected mounts).
deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    count(object.get(props, "volumeMounts", [])) == 0
    some mount in container.volumeMounts
    not is_k8s_injected_mount(mount)
    msg := sprintf(
        "containers '%s': pod has volume mount '%s' but policy does not allow volume mounts",
        [container.name, mount.name],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    some policy_mount in props.volumeMounts
    pod_mount := volume_mount_by_name(container, policy_mount.name)
    pod_mount.mountPath != policy_mount.mountPath
    msg := sprintf(
        "initContainers '%s': volume mount '%s' mountPath '%s' does not match policy '%s'",
        [container.name, policy_mount.name, pod_mount.mountPath, policy_mount.mountPath],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    some policy_mount in props.volumeMounts
    pod_mount := volume_mount_by_name(container, policy_mount.name)
    pod_mount.readOnly != policy_mount.readOnly
    msg := sprintf(
        "initContainers '%s': volume mount '%s' readOnly=%v does not match policy readOnly=%v",
        [container.name, policy_mount.name, pod_mount.readOnly, policy_mount.readOnly],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    some policy_mount in props.volumeMounts
    not has_volume_mount(container, policy_mount.name)
    msg := sprintf(
        "initContainers '%s': volume mount '%s' required by policy but not found in pod",
        [container.name, policy_mount.name],
    )
}

# Pod mount must exist in policy (initContainers, skip Kubernetes auto-injected mounts).
deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    count(object.get(props, "volumeMounts", [])) > 0
    some mount in container.volumeMounts
    not is_k8s_injected_mount(mount)
    not policy_has_volume_mount(props, mount.name)
    msg := sprintf(
        "initContainers '%s': volume mount '%s' found in pod but not in policy",
        [container.name, mount.name],
    )
}

# Pod has volume mounts but policy does not specify any (initContainers).
deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    count(object.get(props, "volumeMounts", [])) == 0
    some mount in container.volumeMounts
    not is_k8s_injected_mount(mount)
    msg := sprintf(
        "initContainers '%s': pod has volume mount '%s' but policy does not allow volume mounts",
        [container.name, mount.name],
    )
}

# Kubernetes auto-injected mounts must be readOnly.
deny contains msg if {
    some container in input.spec.containers
    some mount in container.volumeMounts
    is_k8s_injected_mount(mount)
    not object.get(mount, "readOnly", false)
    msg := sprintf(
        "containers '%s': Kubernetes injected mount '%s' at '%s' must be readOnly",
        [container.name, mount.name, mount.mountPath],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    some mount in container.volumeMounts
    is_k8s_injected_mount(mount)
    not object.get(mount, "readOnly", false)
    msg := sprintf(
        "initContainers '%s': Kubernetes injected mount '%s' at '%s' must be readOnly",
        [container.name, mount.name, mount.mountPath],
    )
}

# Check if a volume mount is a Kubernetes auto-injected mount (kube-api-access-*
# projected volume at the service account path).
is_k8s_injected_mount(mount) if {
    startswith(mount.name, "kube-api-access-")
    mount.mountPath == "/var/run/secrets/kubernetes.io/serviceaccount"
}

# Find a volume mount by name, skipping Kubernetes auto-injected mounts.
volume_mount_by_name(container, name) := mount if {
    some mount in container.volumeMounts
    mount.name == name
    not is_k8s_injected_mount(mount)
}

has_volume_mount(container, name) if {
    some mount in container.volumeMounts
    mount.name == name
}

# Check if a policy has a volume mount with the given name.
policy_has_volume_mount(props, name) if {
    some policy_mount in props.volumeMounts
    policy_mount.name == name
}

# --- Security context checks ---

deny contains msg if {
    some container in input.spec.containers
    props := policy_by_name[container.name]
    pod_privileged := object.get(
        object.get(container, "securityContext", {}),
        "privileged", false,
    )
    policy_privileged := object.get(props, "privileged", false)
    pod_privileged != policy_privileged
    msg := sprintf(
        "containers '%s': privileged=%v does not match policy privileged=%v",
        [container.name, pod_privileged, policy_privileged],
    )
}

deny contains msg if {
    some container in input.spec.initContainers
    props := policy_by_name[container.name]
    pod_privileged := object.get(
        object.get(container, "securityContext", {}),
        "privileged", false,
    )
    policy_privileged := object.get(props, "privileged", false)
    pod_privileged != policy_privileged
    msg := sprintf(
        "initContainers '%s': privileged=%v does not match policy privileged=%v",
        [container.name, pod_privileged, policy_privileged],
    )
}

# Expose deny reasons as the "reasons" set for the Go code to read.
reasons := deny
