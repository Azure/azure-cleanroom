#!/usr/bin/env python3
"""
Shared Cleanroom Environment Setup

This script handles the deployment of cleanroom infrastructure (virtual or AKS/ACI),
executing governance and cluster deployments in parallel. It saves the deployment
configuration to a JSON file for use by subsequent test scripts.
"""

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

_git_root = subprocess.run(
    ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
).stdout.strip()
sys.path.insert(0, str(Path(_git_root) / "test" / "onebox"))
from cleanroom_test_utils import (
    Colors,
    deploy_aks_cluster_governance,
    deploy_virtual_cluster_governance,
    get_unique_string,
    run_command,
)


def main():
    parser = argparse.ArgumentParser(
        description="Setup environment (virtual or AKS/ACI)"
    )
    parser.add_argument(
        "--infra-type",
        choices=["virtual", "aks"],
        default="virtual",
        help="Infrastructure type (default: virtual)",
    )
    parser.add_argument(
        "--no-build",
        type=lambda x: x.lower() in ["true", "1", "yes"],
        default=False,
        help="Skip building container images (true/false, 1/0, yes/no)",
    )
    parser.add_argument(
        "--registry",
        choices=["mcr", "local", "acr"],
        default="local",
        help="Container registry to use",
    )
    parser.add_argument(
        "--repo",
        default="localhost:5000",
        help="Container repository",
    )
    parser.add_argument(
        "--tag",
        default="latest",
        help="Container image tag",
    )
    parser.add_argument(
        "--allow-all",
        type=lambda x: x.lower() in ["true", "1", "yes"],
        default=False,
        help="Allow all security policies (for AKS only)",
    )
    parser.add_argument(
        "--enable-monitoring",
        type=lambda x: x.lower() in ["true", "1", "yes"],
        default=False,
        help="Enable monitoring for the cluster",
    )
    parser.add_argument(
        "--max-workers",
        type=int,
        default=2,
        help="Maximum number of parallel workers for deployment (default: 2)",
    )
    parser.add_argument(
        "--out-dir",
        required=True,
        help="Output directory for deployment artifacts",
    )
    parser.add_argument(
        "--ccf-project-name",
        required=True,
        help="CCF project name",
    )
    parser.add_argument(
        "--project-name",
        required=True,
        help="Governance project name",
    )
    parser.add_argument(
        "--initial-member-name",
        required=True,
        help="Initial member name for governance",
    )
    parser.add_argument(
        "--cluster-provider-project-name",
        required=True,
        help="Cluster provider project name",
    )
    parser.add_argument(
        "--cluster-name",
        required=True,
        help="Cluster name",
    )
    parser.add_argument(
        "--ccf-provider-project-name",
        help="CCF provider project name (for AKS only)",
    )
    parser.add_argument(
        "--location",
        default="centralindia",
        help="Azure region for resource deployment (default: centralindia)",
    )

    args = parser.parse_args()

    # Validation for AKS
    if args.infra_type == "aks":
        if args.registry == "local":
            print(
                f"{Colors.RED}Cannot use 'local' registry with AKS infrastructure. Use 'acr' or 'mcr'.{Colors.RESET}"
            )
            sys.exit(1)
        if args.registry == "acr" and not args.repo:
            print(
                f"{Colors.RED}-repo must be specified when using 'acr' registry.{Colors.RESET}"
            )
            sys.exit(1)
        if not args.ccf_provider_project_name:
            print(
                f"{Colors.RED}--ccf-provider-project-name is required for AKS infrastructure.{Colors.RESET}"
            )
            sys.exit(1)

    out_dir = args.out_dir

    # Get repository root
    result = run_command("git rev-parse --show-toplevel")
    root = result.stdout.strip()

    # Determine registry settings
    if args.registry == "mcr":
        using_registry = "mcr"
        registry_arg = "mcr"
    elif args.registry == "acr":
        using_registry = args.repo
        registry_arg = "acr"
    else:  # local
        using_registry = f"{args.registry} ({args.repo})"
        registry_arg = "local"

    print(f"Using {using_registry} registry for cleanroom container images.")

    # Deploy based on infrastructure type
    if args.infra_type == "virtual":
        # Virtual deployment
        success, ccf_endpoint = deploy_virtual_cluster_governance(
            root=root,
            out_dir=out_dir,
            no_build=args.no_build,
            registry=args.registry,
            repo=args.repo,
            tag=args.tag,
            ccf_project_name=args.ccf_project_name,
            project_name=args.project_name,
            initial_member_name=args.initial_member_name,
            cluster_provider_project_name=args.cluster_provider_project_name,
            cluster_name=args.cluster_name,
            enable_monitoring=args.enable_monitoring,
            max_workers=args.max_workers,
        )
    else:  # aks
        # Determine resource groups and names
        if os.environ.get("GITHUB_ACTIONS") == "true":
            job_id = os.environ.get("JOB_ID", "")
            run_id = os.environ.get("RUN_ID", "")
            run_attempt = os.environ.get("RUN_ATTEMPT", "")

            unique_string = get_unique_string(f"cleanroom-cluster-{job_id}-{run_id}")
            cl_cluster_resource_group = f"rg-{unique_string}"
            isv_resource_group = f"cl-ob-isv-{job_id}-{run_id}-{run_attempt}"
            resource_group_tags = f"github_actions={job_id}-{run_id}"
        else:
            user = (
                os.environ.get("GITHUB_USER")
                if os.environ.get("CODESPACES") == "true"
                else os.environ.get("USER", "unknown")
            )
            isv_resource_group = f"cl-ob-isv-ccf-{user}"
            cl_cluster_resource_group = f"cl-ob-isv-cl-{user}"
            resource_group_tags = ""

        ccf_name = f"{get_unique_string(isv_resource_group)}-ccf"
        cluster_name = f"{get_unique_string(cl_cluster_resource_group)}-cluster"
        location = args.location

        # Create resource groups
        print(f"Creating resource group {isv_resource_group} in {location}")
        rg_cmd = f"az group create --location {location} --name {isv_resource_group}"
        if resource_group_tags:
            rg_cmd += f" --tags {resource_group_tags}"
        run_command(rg_cmd)

        print(f"Creating resource group {cl_cluster_resource_group} in {location}")
        rg_cmd = (
            f"az group create --location {location} --name {cl_cluster_resource_group}"
        )
        if resource_group_tags:
            rg_cmd += f" --tags {resource_group_tags}"
        run_command(rg_cmd)

        # AKS deployment
        success, ccf_endpoint = deploy_aks_cluster_governance(
            root=root,
            out_dir=out_dir,
            no_build=args.no_build,
            registry=args.registry,
            repo=args.repo,
            tag=args.tag,
            allow_all=args.allow_all,
            isv_resource_group=isv_resource_group,
            cl_cluster_resource_group=cl_cluster_resource_group,
            ccf_name=ccf_name,
            cluster_name=cluster_name,
            location=location,
            project_name=args.project_name,
            initial_member_name=args.initial_member_name,
            ccf_provider_project_name=args.ccf_provider_project_name,
            cluster_provider_project_name=args.cluster_provider_project_name,
            max_workers=args.max_workers,
        )

    if not success:
        print(f"{Colors.RED}Deployment failed. Exiting.{Colors.RESET}")
        sys.exit(1)

    # Save deployment configuration
    config = {
        "infra_type": args.infra_type,
        "ccf_endpoint": ccf_endpoint,
        "registry": args.registry,
        "registry_arg": registry_arg,
        "repo": args.repo,
        "tag": args.tag,
        "out_dir": out_dir,
        "allow_all": args.allow_all,
        "project_name": args.project_name,
        "initial_member_name": args.initial_member_name,
        "cluster_provider_project_name": args.cluster_provider_project_name,
    }

    config_file = Path(out_dir) / "deployment-config.json"
    with open(config_file, "w") as f:
        json.dump(config, f, indent=2)

    print(f"\n{Colors.GREEN}✓ Environment setup completed successfully!{Colors.RESET}")
    print(f"{Colors.CYAN}Configuration saved to: {config_file}{Colors.RESET}")
    print(f"{Colors.CYAN}CCF Endpoint: {ccf_endpoint}{Colors.RESET}")


if __name__ == "__main__":
    main()
