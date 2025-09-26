import argparse
import base64
import json
import os
from urllib.parse import urlunparse, urlunsplit

import yaml


def replace_registry_url(url, registry_url):
    # url parse does not work if scheme is not specified. Work around this limitation by
    # adding a http to the url.
    if not url.startswith("http"):
        url = "http://" + url
    url_parsed = urlparse(url)
    # Replace the netloc with the registry override.
    url_parsed = url_parsed._replace(netloc=registry_url)
    url = urlunparse(url_parsed)
    url = re.sub("https?://", "", url)
    return url


parser = argparse.ArgumentParser(
    description="Convert an Clean Room ACI ARM template to a k8s deployment spec for local testing."
)
parser.add_argument(
    "--template-file",
    type=str,
    dest="template_file",
    required=True,
    help="The ARM template file to convert.",
)
parser.add_argument(
    "--registry-local-endpoint",
    type=str,
    dest="registry_local_endpoint",
    required=False,
    default=None,
    help="The local endpoint to use for the registry. This will be used by containers looking to access the registry from within the cleanroom.",
)
parser.add_argument(
    "--repo",
    type=str,
    dest="repo",
    required=True,
    help="The registry endpoint to download the container images.",
)
parser.add_argument(
    "--tag",
    type=str,
    dest="tag",
    required=True,
    help="The tag for downloading the container images.",
)
parser.add_argument(
    "--out-dir",
    type=str,
    dest="out_dir",
    required=True,
    help="The output directory to create the deployment spec.",
)
parser.add_argument(
    "--name",
    type=str,
    dest="pod_name",
    required=False,
    default="virtual-cleanroom",
    help="The pod name to use in the pod spec. Defaults to virtual-cleanroom.",
)

args = parser.parse_args()

with open(args.template_file) as f:
    parsed_json = json.load(f)

# Map init containers.
pod_init_containers = []
for container in parsed_json["resources"][0]["properties"]["initContainers"]:
    pod_init_container = {
        "name": container["name"],
        "image": container["properties"]["image"],
    }
    if "command" in container["properties"]:
        pod_init_container["command"] = container["properties"]["command"]

    privileged = False
    if "securityContext" in container["properties"]:
        if "privileged" in container["properties"]["securityContext"]:
            pod_init_container["securityContext"] = {
                "privileged": (
                    True
                    if container["properties"]["securityContext"]["privileged"]
                    == "true"
                    else False
                )
            }
            privileged = pod_init_container["securityContext"]["privileged"]

    if "environmentVariables" in container["properties"]:
        pod_init_container["env"] = []
        for item in container["properties"]["environmentVariables"]:
            pod_init_container["env"].append(
                {"name": item["name"], "value": item["value"]}
            )

    if "volumeMounts" in container["properties"]:
        pod_init_container["volumeMounts"] = []
        for item in container["properties"]["volumeMounts"]:
            pod_init_container["volumeMounts"].append(
                {
                    "name": item["name"],
                    "mountPath": item["mountPath"],
                    "mountPropagation": (
                        "Bidirectional" if privileged else "HostToContainer"
                    ),
                }
            )

    pod_init_containers.append(pod_init_container)

# Map containers.
pod_containers = []
for container in parsed_json["resources"][0]["properties"]["containers"]:
    pod_container = {
        "name": container["name"],
        "image": container["properties"]["image"],
    }

    if "command" in container["properties"]:
        pod_container["command"] = container["properties"]["command"]

    privileged = False
    if "securityContext" in container["properties"]:
        if "privileged" in container["properties"]["securityContext"]:
            pod_container["securityContext"] = {
                "privileged": (
                    True
                    if container["properties"]["securityContext"]["privileged"]
                    == "true"
                    or container["properties"]["securityContext"]["privileged"] == True
                    else False
                )
            }
            privileged = pod_container["securityContext"]["privileged"]

    if "environmentVariables" in container["properties"]:
        pod_container["env"] = []
        for item in container["properties"]["environmentVariables"]:
            pod_container["env"].append({"name": item["name"], "value": item["value"]})

    if "volumeMounts" in container["properties"]:
        pod_container["volumeMounts"] = []
        for item in container["properties"]["volumeMounts"]:
            pod_container["volumeMounts"].append(
                {
                    "name": item["name"],
                    "mountPath": item["mountPath"],
                    "mountPropagation": (
                        "Bidirectional" if privileged else "HostToContainer"
                    ),
                }
            )

    pod_containers.append(pod_container)

# Map volumes containers.
volumes = []
if "volumes" in parsed_json["resources"][0]["properties"]:
    volumes = parsed_json["resources"][0]["properties"]["volumes"]

# Replace/update/remove few containers for local testing scenario.
# Remove ccr-attestation as it is not required in local env.
# Replace skr-sidecar with the local-skr container.
# Replace ccr-governance sidecar with its virtual image that has the attestation report/keys baked in.
# Remove CCR_FQDN env variable from ccr-proxy.
pod_containers = [pc for pc in pod_containers if pc["name"] != "ccr-attestation"]
pod_containers = [pc for pc in pod_containers if pc["name"] != "skr-sidecar"]
pod_containers.append(
    {
        "name": "local-skr-sidecar",
        "image": f"{args.repo}/local-skr:{args.tag}",
    }
)
for item in pod_containers:
    if item["name"] == "ccr-governance":
        item["image"] = f"{args.repo}/ccr-governance-virtual:{args.tag}"
    elif "ccr-proxy-ext-processor" in item["name"]:
        import re
        from urllib.parse import urlparse, urlunparse

        bundleResourcePath = [
            x for x in item["env"] if x["name"] == "BUNDLE_RESOURCE_PATH"
        ][0]["value"]

        if "localhost" in bundleResourcePath:
            if args.registry_local_endpoint:
                item["env"].append({"name": "USE_HTTP", "value": "true"})
                bundleResourcePath = replace_registry_url(
                    bundleResourcePath, args.registry_local_endpoint
                )
                [x for x in item["env"] if x["name"] == "BUNDLE_RESOURCE_PATH"][0][
                    "value"
                ] = bundleResourcePath
    elif "code-launcher" in item["name"]:
        import re
        from urllib.parse import urlparse, urlunparse

        application_details_index = [
            i for i, e in enumerate(item["command"]) if e == "--application-base-64"
        ][0]
        application_details = base64.b64decode(
            item["command"][application_details_index + 1]
        ).decode("utf-8")
        application = json.loads(application_details)
        if (
            "localhost"
            in application["image"]["executable"]["backingResource"]["provider"]["url"]
        ):
            if args.registry_local_endpoint:
                application["image"]["executable"]["backingResource"]["provider"][
                    "url"
                ] = args.registry_local_endpoint
                application["image"]["executable"]["backingResource"]["id"] = (
                    replace_registry_url(
                        application["image"]["executable"]["backingResource"]["id"],
                        args.registry_local_endpoint,
                    )
                )
                item["command"][application_details_index + 1] = base64.b64encode(
                    json.dumps(application).encode("utf-8")
                ).decode("utf-8")

        # Add the registry-conf volume mount to the code-launcher container.
        # This will be backed by the config-map ccr-registry-conf created in kind cluster.
        # This ensures that the code-launcher container accesses the local registry with "http"
        # instead of "https".
        item["volumeMounts"].append(
            {
                "name": "registry-conf",
                "mountPath": "/etc/containers/registries.conf.d",
            }
        )
        volumes.append(
            {
                "name": "registry-conf",
                "configMap": {
                    "name": "ccr-registry-conf",
                    "items": [
                        {"key": "ccr-registry.conf", "path": "ccr-registry.conf"}
                    ],
                },
            }
        )
    elif item["name"] == "ccr-proxy":
        item["env"] = [e for e in item["env"] if e["name"] != "CCR_FQDN"]

output = {
    "apiVersion": "v1",
    "kind": "Pod",
    "metadata": {"name": args.pod_name, "labels": {"app": args.pod_name}},
    "spec": {
        "initContainers": pod_init_containers,
        "containers": pod_containers,
        "volumes": volumes,
        "restartPolicy": "Never",
    },
}

out_file = os.path.join(args.out_dir, f"{args.pod_name}-pod.yaml")
with open(out_file, "w") as yaml_file:
    yaml.dump(output, yaml_file, default_flow_style=False, sort_keys=False)
