import argparse
import json
import os
import subprocess
import logging
import sys

import jinja2


template_path = os.path.join(os.path.dirname(__file__), "templates")


def generate_config(template_path, template, output_path, **context):
    """Generate a final config file based on a template and some context."""
    env = jinja2.Environment(
        loader=jinja2.FileSystemLoader(template_path, followlinks=True),
        undefined=jinja2.StrictUndefined,
    )
    rendered_template = env.get_template(template).render(**context)
    with open(output_path, "w") as f:
        f.write(rendered_template)


def log_args(logger: logging.Logger, args: argparse.Namespace):
    logger.info("Arguments:")
    for arg in vars(args):
        logger.info(f"{arg}: {getattr(args, arg)}")


def parse_args():
    arg_parser = argparse.ArgumentParser(description="Init container for cleanroom.")
    # Telemetry params
    arg_parser.add_argument(
        "--clear-mount-paths",
        type=str,
        nargs="*",
        help="The list of mount paths to clear before other containers are started.",
    )
    arg_parser.add_argument(
        "--create-directories",
        type=str,
        nargs="*",
        help="The list of directories to create for the cleanroom.",
    )
    arg_parser.add_argument(
        "--allowed-ips",
        help="The network IPs to which egress is allowed from the clean room. Please specify a json array like '[{'address': '<IP_ADDRESS>', 'port': '<PORT>'}, ...]'",
        type=json.loads,
        default=[],
    )
    arg_parser.add_argument(
        "--enable-dns",
        action="store_true",
        help="Whether to allow enable DNS traffic.",
    )
    arg_parser.add_argument(
        "--dns-port",
        type=int,
        default=53,
        help="The port where the DNS traffic is going to.",
    )
    return arg_parser.parse_args()


def main():
    logger = logging.getLogger()
    logging.basicConfig(stream=sys.stdout, level=logging.DEBUG)
    args = parse_args()
    log_args(logger, args)

    if args.clear_mount_paths:
        logging.info(f"Clearing specified mount paths: {args.clear_mount_paths}")
        for mount_path in args.clear_mount_paths:
            try:
                os.remove(f"{mount_path}/*")
            except FileNotFoundError:
                pass
            except Exception as e:
                logger.error(f"Failed to clear mount path {mount_path}. Error: {e}")
                raise

    if args.create_directories:
        logging.info(f"Creating specified directories: {args.create_directories}")
        for directory in args.create_directories:
            try:
                os.makedirs(directory, exist_ok=True)
                os.chmod(directory, 0o777)
            except FileExistsError:
                pass
            except Exception as e:
                logger.error(f"Failed to create directory {directory}. Error: {e}")
                raise

    logger.info("Generating config files...")
    iptables_config_file = "setup-iptables.sh"
    generate_config(
        template_path,
        "setup-iptables.sh.j2",
        iptables_config_file,
        allowed_ips=args.allowed_ips,
        enable_dns=args.enable_dns,
        dns_port=args.dns_port,
    )
    os.chmod(iptables_config_file, 0o755)

    try:
        proc = subprocess.run(
            f"bash {iptables_config_file}",
            shell=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=True,
        )
        logger.info("Successfully executed iptables rules.")
        logger.info(f"Output: {proc.stdout}")

    except subprocess.CalledProcessError as e:
        logger.error(f"Failed to launch subprocess. Error: {e}")
        logger.error(f"Output: {e.output}")
        logger.error(f"Error: {e.stderr}")
        raise


if __name__ == "__main__":
    main()
