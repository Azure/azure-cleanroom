import argparse
import logging
import os
import sys

import jinja2
import pydantic


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
    arg_parser = argparse.ArgumentParser(
        description="Network proxy container for cleanroom."
    )
    # Telemetry params
    arg_parser.add_argument(
        "--allow-http-outbound-access",
        type=bool,
        default=pydantic.TypeAdapter(bool).validate_python(
            os.environ.get("ALLOW_HTTP_OUTBOUND_ACCESS", "False")
        ),
        help="Whether to allow outbound HTTP access.",
    )
    arg_parser.add_argument(
        "--allow-http-inbound-access",
        type=bool,
        default=pydantic.TypeAdapter(bool).validate_python(
            os.environ.get("ALLOW_HTTP_INBOUND_ACCESS", "False")
        ),
        help="Whether to allow inbound HTTP access.",
    )
    arg_parser.add_argument(
        "--allow-tcp-outbound-access",
        type=bool,
        default=pydantic.TypeAdapter(bool).validate_python(
            os.environ.get("ALLOW_TCP_OUTBOUND_ACCESS", "False")
        ),
        help="Whether to allow outbound TCP access.",
    )
    arg_parser.add_argument(
        "--template-path",
        type=str,
        help="Path to the template files.",
    )
    arg_parser.add_argument(
        "--output-path",
        type=str,
        help="Path to output the rendered templates.",
    )
    return arg_parser.parse_args()


def main():
    logger = logging.getLogger()
    logging.basicConfig(stream=sys.stdout, level=logging.DEBUG)
    args = parse_args()
    log_args(logger, args)
    logger.info("Generating config files...")
    generate_config(
        os.path.join(args.template_path, "lds"),
        "lds.template.yaml.jinja",
        os.path.join(args.output_path, "lds.yaml"),
        allow_http_outbound_access=args.allow_http_outbound_access,
        allow_http_inbound_access=args.allow_http_inbound_access,
        allow_tcp_outbound_access=args.allow_tcp_outbound_access,
    )

    generate_config(
        os.path.join(args.template_path, "cds"),
        "cds.template.yaml.jinja",
        os.path.join(args.output_path, "cds.yaml"),
        allow_http_outbound_access=args.allow_http_outbound_access,
        allow_http_inbound_access=args.allow_http_inbound_access,
        allow_tcp_outbound_access=args.allow_tcp_outbound_access,
    )


if __name__ == "__main__":
    main()
