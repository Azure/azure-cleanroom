#!/usr/bin/env python3
"""
Generate TypeSpec operation files (app.tsp and interfaces.tsp) from CCF app.json

This script reads the CCF governance app.json file and generates two TypeSpec files:
1. app.tsp - HTTP API routes and operations for the governance service
2. interfaces.tsp - Client library interfaces abstracting HTTP details
"""

import argparse
import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List


@dataclass
class Endpoint:
    route: str
    method: str
    function: str
    module: str
    auth_policies: List[str]
    openapi: Dict[str, Any]
    mode: str
    request: str | None
    response: str | None


MODULE_NAME_OVERRIDES: Dict[str, str] = {}


def get_module_key(js_module: str) -> str:
    """Extract the module key from a js_module path.

    E.g., 'endpoints/certificate-authority/ca-key.js' -> 'certificate-authority',
          'endpoints/contracts.js' -> 'contracts'
    """
    return js_module.split("endpoints/")[1].split("/")[0].split(".")[0]


def get_interface_name(module_key: str) -> str:
    """Derive PascalCase interface name from a kebab-case module key.

    E.g., 'certificate-authority' -> 'CertificateAuthority',
          'clean-room-policy' -> 'CleanRoomPolicy'
    """
    if module_key in MODULE_NAME_OVERRIDES:
        return MODULE_NAME_OVERRIDES[module_key]
    return "".join(word.capitalize() for word in module_key.split("-"))


def compute_base_route(routes: List[str]) -> str:
    """Compute the base route as the longest common path prefix of all routes.

    Deprecated endpoints are filtered before grouping, so all routes within a
    group share a meaningful common prefix.
    """
    if len(routes) == 1:
        return routes[0]
    return os.path.commonprefix(routes).rstrip("/")


def parse_app_json(app_json_path: Path) -> Dict[str, List[Endpoint]]:
    """Parse app.json and group endpoints by module key (kebab-case)."""
    with open(app_json_path, "r") as f:
        app_json = json.load(f)

    endpoint_groups = {}

    for route, methods in app_json["endpoints"].items():
        for method, endpoint in methods.items():
            is_deprecated = False
            if (endpoint["openapi"] is not None) and (
                "deprecated" in endpoint["openapi"]
                and endpoint["openapi"]["deprecated"]
            ):
                is_deprecated = True

            request = None
            if (
                (endpoint["openapi"] is not None)
                and ("requestBody" in endpoint["openapi"])
                and ("content" in endpoint["openapi"]["requestBody"])
            ):
                request = endpoint["openapi"]["requestBody"]["content"][
                    "application/json"
                ]["schema"]["type"]

            response = None
            if (
                (endpoint["openapi"] is not None)
                and ("responses" in endpoint["openapi"])
                and ("200" in endpoint["openapi"]["responses"])
            ):
                resp200 = endpoint["openapi"]["responses"]["200"]
                if "content" in resp200:
                    response = resp200["content"]["application/json"]["schema"]["type"]
                else:
                    # 200 with no body — use EmptyResponse model
                    # which maps to HTTP 200 OK with no content.
                    response = "EmptyResponse"

            if not is_deprecated:
                raw_policies = endpoint.get("authn_policies", [])
                auth_policies = [
                    "attestation" if p == "no_auth" else p for p in raw_policies
                ] or ["attestation"]

                ep = Endpoint(
                    route=route,
                    method=method.upper(),
                    function=endpoint["js_function"],
                    module=endpoint["js_module"],
                    auth_policies=auth_policies,
                    mode=endpoint["mode"],
                    openapi=endpoint["openapi"],
                    request=request,
                    response=response,
                )

                module_key = get_module_key(ep.module)
                if module_key not in endpoint_groups:
                    endpoint_groups[module_key] = []

                endpoint_groups[module_key].append(ep)
            else:
                print(f"Skipping deprecated endpoint: {method.upper()} {route}")

    return endpoint_groups


def extract_path_params(route: str) -> List[str]:
    """Extract path parameters from route"""
    return re.findall(r"\{(\w+)\}", route)


def extract_query_params(openapi: Dict[str, Any]) -> Dict[str, str]:
    """Extract query parameters from OpenAPI spec"""
    params = {}
    for param in openapi.get("parameters", []):
        if param.get("in") == "query":
            name = param.get("name")
            if not param.get("required", False):
                name = name + "?"
            schema = param.get("schema", {})
            type_ = schema.get("type", "string")
            params[name] = type_
    return params


def get_operation_name(function: str, method: str, group_name: str) -> str:
    """Generate operation name from function name"""
    # Default: lowercase first letter
    return function[0].lower() + function[1:] if function else function


def get_return_type(ep: Endpoint, op_name: str, group_name: str) -> str:
    """Determine return type for an operation"""
    if ep.response is not None:
        return ep.response

    return "void"


def get_request_type(ep: Endpoint, op_name: str, group_name: str) -> str | None:
    """Determine request parameter type"""
    if ep.request is not None:
        return f"@body request: {ep.request}"

    return None


def get_doc_comment(ep: Endpoint, op_name: str, group_name: str) -> str:
    """Generate documentation comment"""
    policies_str = ", ".join(ep.auth_policies)
    comment = f"\n    {ep.function}\n    {ep.module}\n    {ep.method}\n    AuthPolicies: [{policies_str}]\n"
    return comment


def get_auth_policies_decorators(ep: Endpoint) -> list[str]:
    """Generate @tag decorators for auth policies from endpoint auth policies."""
    return [f'@tag("auth:{p}")' for p in ep.auth_policies]


def generate_api_tsp_interface(group_name: str, endpoints: List[Endpoint]) -> str:
    """Generate a TypeSpec interface for app.tsp"""
    base_route = compute_base_route([ep.route for ep in endpoints])

    lines = []
    lines.append(f'@route("{base_route}")')
    lines.append(f"interface {group_name} {{")

    for ep in endpoints:
        # Generate operation name
        op_name = get_operation_name(ep.function, ep.method, group_name)

        # Generate comment
        doc = get_doc_comment(ep, op_name, group_name)
        lines.append(f"  /** {doc}  */")

        # Generate auth policies decorator
        for tag in get_auth_policies_decorators(ep):
            lines.append(f"  {tag}")

        # Generate HTTP method decorator
        method_lower = ep.method.lower()
        lines.append(f"  @{method_lower}")

        # Generate route if not base
        sub_route = ep.route[len(base_route) :]
        if sub_route:
            lines.append(f'  @route("{sub_route}")')

        # Generate operation parameters
        path_params = extract_path_params(ep.route)
        base_params = extract_path_params(base_route)

        params = []
        # Add base route params first (contractId, documentId, etc.)
        for param in base_params:
            params.append(f"@path {param}: string")

        # Add path params that are in the route but not in base
        for param in path_params:
            if param not in base_params:
                params.append(f"@path {param}: string")

        # Add query parameters from OpenAPI spec
        query_params = extract_query_params(ep.openapi)
        for param_name, param_type in query_params.items():
            params.append(f"@query {param_name}: {param_type}")

        # Add body parameter
        request_type = get_request_type(ep, op_name, group_name)
        if request_type:
            params.append(request_type)

        # Format parameters as simple comma-separated list
        params_str = ", ".join(params) if params else ""

        # Get return type
        return_type = get_return_type(ep, op_name, group_name)

        lines.append(f"  {op_name}({params_str}): {return_type};")
        lines.append("")  # Blank line after each operation

    # Remove the last blank line
    if lines and lines[-1] == "":
        lines.pop()

    lines.append("}")

    return "\n".join(lines)


def generate_app_tsp(endpoint_groups: Dict[str, List[Endpoint]], output_path: Path):
    """Generate app.tsp import hub and per-interface files in app/ directory."""
    app_dir = output_path.parent / "app"
    app_dir.mkdir(parents=True, exist_ok=True)

    import_lines = []
    import_lines.append('import "@typespec/http";')
    import_lines.append('import "./models.tsp";')
    import_lines.append("")

    for module_key in sorted(endpoint_groups.keys()):
        group_name = get_interface_name(module_key)
        endpoints = endpoint_groups[module_key]

        # Generate per-interface file
        lines = []
        lines.append('import "@typespec/http";')
        lines.append('import "../models.tsp";')
        lines.append("")
        lines.append("using Http;")
        lines.append("")
        lines.append("namespace Azure.Cleanroom.Governance;")
        lines.append("")
        lines.append(generate_api_tsp_interface(group_name, endpoints))
        lines.append("")

        file_path = app_dir / f"{module_key}.tsp"
        with open(file_path, "w") as f:
            f.write("\n".join(lines))
        print(f"Generated: {file_path}")

        import_lines.append(f'import "./app/{module_key}.tsp";')

    import_lines.append("")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w") as f:
        f.write("\n".join(import_lines))

    print(f"Generated: {output_path}")


def generate_client_interface(group_name: str, endpoints: List[Endpoint]) -> str:
    """Generate a client interface for interfaces.tsp"""
    lines = []
    lines.append("/**")
    lines.append(f" * {group_name} operations")
    lines.append(" */")
    lines.append(f"interface {group_name} {{")

    for ep in endpoints:
        # Generate operation name
        op_name = get_operation_name(ep.function, ep.method, group_name)

        # Extract parameters
        path_params = extract_path_params(ep.route)
        params = [f"{param}: string" for param in path_params]

        # Add additional params based on operation type
        if ep.method in ["PUT", "POST"]:
            if ep.request is not None:
                params.append(f"request: {ep.request}")

        return_type = "void"
        if ep.response is not None:
            return_type = ep.response

        params_str = ", ".join(params)

        # Add lightweight doc comment with actual auth policies.
        policies_str = ", ".join(ep.auth_policies)
        lines.append(f"  /** {ep.function} (AuthPolicies: [{policies_str}]) */")

        # Add auth policies decorator.
        for tag in get_auth_policies_decorators(ep):
            lines.append(f"  {tag}")

        lines.append(f"  {op_name}({params_str}): {return_type};")

    lines.append("}")

    return "\n".join(lines)


def generate_interfaces_tsp(
    endpoint_groups: Dict[str, List[Endpoint]], output_path: Path
):
    """Generate interfaces.tsp import hub and per-interface files in interfaces/ dir."""
    interfaces_dir = output_path.parent / "interfaces"
    interfaces_dir.mkdir(parents=True, exist_ok=True)

    import_lines = []
    import_lines.append('import "./models.tsp";')
    import_lines.append("")

    all_interface_names = []
    for module_key in sorted(endpoint_groups.keys()):
        group_name = get_interface_name(module_key)
        endpoints = endpoint_groups[module_key]

        # Generate per-interface file
        lines = []
        lines.append('import "../models.tsp";')
        lines.append("")
        lines.append("using Azure.Cleanroom.Governance;")
        lines.append("")
        lines.append("namespace Azure.Cleanroom.SDK;")
        lines.append("")
        lines.append(generate_client_interface(group_name, endpoints))
        lines.append("")

        file_path = interfaces_dir / f"{module_key}.tsp"
        with open(file_path, "w") as f:
            f.write("\n".join(lines))
        print(f"Generated: {file_path}")

        import_lines.append(f'import "./interfaces/{module_key}.tsp";')
        all_interface_names.append(group_name)

    import_lines.append("")

    # Generate composite GovernanceClient interface in the hub file
    import_lines.append("using Azure.Cleanroom.Governance;")
    import_lines.append("")
    import_lines.append("namespace Azure.Cleanroom.SDK;")
    import_lines.append("")
    import_lines.append("/**")
    import_lines.append(" * Complete Governance Client interface")
    import_lines.append(
        " * Aggregates all governance operations across all authentication modes"
    )
    import_lines.append(" */")
    import_lines.append("interface GovernanceClient")

    extends = ",\n    ".join(all_interface_names)
    import_lines.append(f"  extends {extends} {{")
    # Note: getCapabilities is hand-crafted in the SDK as
    # CleanroomClientCapabilities, not generated from TypeSpec.
    import_lines.append("}")
    import_lines.append("")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w") as f:
        f.write("\n".join(import_lines))

    print(f"Generated: {output_path}")


def main():
    parser = argparse.ArgumentParser(
        description="Generate TypeSpec operation files from CCF app.json"
    )
    parser.add_argument("--app-json", type=Path, help="Path to app.json file")
    parser.add_argument(
        "--output-dir", type=Path, help="Base directory for TypeSpec output"
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Show what would be generated without writing files",
    )

    args = parser.parse_args()

    # Determine paths
    try:
        import subprocess

        root = Path(
            subprocess.check_output(
                ["git", "rev-parse", "--show-toplevel"], text=True
            ).strip()
        )
    except:
        root = Path.cwd()

    app_json_path = args.app_json or root / "src/governance/ccf-app/js/app.json"
    output_dir = args.output_dir or root / "src/specifications/typespec"

    api_tsp_path = output_dir / "cleanroom-governance-service/app.tsp"
    interfaces_tsp_path = output_dir / "cleanroom-sdk/interfaces.tsp"

    print("=" * 60)
    print("TypeSpec Operations Generator")
    print("=" * 60)
    print(f"Reading app.json from: {app_json_path}")
    print(f"API output: {api_tsp_path}")
    print(f"Interfaces output: {interfaces_tsp_path}")
    print()

    if not app_json_path.exists():
        print(f"ERROR: app.json not found at: {app_json_path}")
        return 1

    # Parse app.json
    endpoint_groups = parse_app_json(app_json_path)

    # Print summary
    total_endpoints = sum(len(eps) for eps in endpoint_groups.values())
    attestation_count = sum(
        1 for eps in endpoint_groups.values() for ep in eps if not ep.auth_policies
    )

    print(f"Loaded {len(endpoint_groups)} interface groups")
    print(f"Total endpoints: {total_endpoints}")
    print(f"Authenticated (jwt/cert): {total_endpoints - attestation_count}")
    print(f"Attestation only: {attestation_count}")
    print()

    if args.dry_run:
        print("DRY RUN: Showing endpoint analysis")
        print()

        for module_key in sorted(endpoint_groups.keys()):
            group_name = get_interface_name(module_key)
            endpoints = endpoint_groups[module_key]
            attestation_count = sum(1 for ep in endpoints if not ep.auth_policies)

            print(f"{group_name} ({module_key}):")
            print(f"  Endpoints: {len(endpoints)}")
            if attestation_count > 0:
                print(f"  Attestation only: {attestation_count}")

            for ep in endpoints:
                policies = ", ".join(ep.auth_policies)
                print(f"    {ep.method} {ep.route} -> {ep.function} [{policies}]")
            print()

        print("No files were written.")
        return 0

    # Generate files
    print("Generating TypeSpec files...")
    print()

    generate_app_tsp(endpoint_groups, api_tsp_path)
    generate_interfaces_tsp(endpoint_groups, interfaces_tsp_path)

    print()
    print("=" * 60)
    print("Generation complete!")
    print("=" * 60)
    print()
    print("Note: Generated files may need manual refinement:")
    print("  - Verify request/response type names")
    print("  - Add missing query parameters")
    print("  - Adjust operation names for clarity")
    print("  - Add comprehensive documentation comments")

    return 0


if __name__ == "__main__":
    exit(main())
