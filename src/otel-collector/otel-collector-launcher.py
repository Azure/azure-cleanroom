import argparse
import logging
import os
import signal
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Dict, Union

import jinja2
from pydantic import BaseModel, Field

logging.basicConfig(
    level=logging.INFO, format="%(asctime)s - %(levelname)s - %(message)s"
)
logger = logging.getLogger(__name__)


def handle_sigterm(signum, frame):
    global args
    logger.info("Received SIGTERM. Cleaning up...")
    sys.exit(0)


signal.signal(signal.SIGTERM, handle_sigterm)


class OtelConfig(BaseModel):
    telemetry_path: str = Field(default=os.getenv("TELEMETRY_PATH") or "")

    prometheus_endpoint: str = Field(default=os.getenv("PROMETHEUS_ENDPOINT") or "")

    loki_endpoint: str = Field(default=os.getenv("LOKI_ENDPOINT") or "")

    tempo_endpoint: str = Field(default=os.getenv("TEMPO_ENDPOINT") or "")

    @property
    def file_exporters_enabled(self) -> bool:
        return self.telemetry_path != ""

    @property
    def prometheus_enabled(self) -> bool:
        return self.prometheus_endpoint != ""

    @property
    def loki_enabled(self) -> bool:
        return self.loki_endpoint != ""

    @property
    def tempo_enabled(self) -> bool:
        return self.tempo_endpoint != ""

    @property
    def prometheus_insecure(self) -> bool:
        return self.prometheus_endpoint.startswith("http://")

    @property
    def loki_insecure(self) -> bool:
        return self.loki_endpoint.startswith("http://")

    @property
    def tempo_insecure(self) -> bool:
        return self.tempo_endpoint.startswith("http://")


def render_template(
    template_path: str, template: str, output_path: str, context: Dict[str, Any]
) -> None:
    try:
        env = jinja2.Environment(
            loader=jinja2.FileSystemLoader(template_path, followlinks=True),
            undefined=jinja2.StrictUndefined,
        )
        rendered_template = env.get_template(template).render(**context)
        with open(output_path, "w") as f:
            f.write(rendered_template)
        logger.info(f"Successfully rendered template to {output_path}")

    except Exception as e:
        logger.error(f"Error rendering template: {e}")
        raise


def main():
    parser = argparse.ArgumentParser(
        description="Generate OTEL Collector configuration and start the collector"
    )
    parser.add_argument(
        "--out-dir",
        type=str,
        default="/var/lib/otel-collector",
        help="Directory for the generated YAML config file (default: /var/lib/otel-collector)",
    )
    args = parser.parse_args()
    script_dir = os.path.dirname(os.path.abspath(__file__))

    collect_telemetry = (
        os.getenv("TELEMETRY_COLLECTION_ENABLED", "false").lower() == "true"
    )
    if not collect_telemetry:
        logger.info("Telemetry collection is disabled. Exiting....")
        sys.exit(1)

    config = OtelConfig()

    try:
        context = config.model_dump()
        context.update(
            {
                "prometheus_insecure": config.prometheus_insecure,
                "loki_insecure": config.loki_insecure,
                "tempo_insecure": config.tempo_insecure,
                "prometheus_enabled": config.prometheus_enabled,
                "loki_enabled": config.loki_enabled,
                "tempo_enabled": config.tempo_enabled,
                "file_exporters_enabled": config.file_exporters_enabled,
            }
        )

        render_template(
            script_dir, "otel-config.yaml.j2", f"{args.out_dir}/config.yaml", context
        )

    except Exception as e:
        logger.error(f"Failed to generate configuration: {e}")
        sys.exit(1)

    try:
        if config.file_exporters_enabled:
            Path(config.telemetry_path).mkdir(parents=True, exist_ok=True)
            logger.info(f"Created telemetry directory at {config.telemetry_path}")

        logger.info("Starting OTEL collector...")
        subprocess.run(
            [
                "/usr/local/bin/otelcol-custom",
                "--config",
                f"{args.out_dir}/config.yaml",
            ],
            check=True,
        )
    except subprocess.CalledProcessError as e:
        logger.error(f"Failed to start OTEL collector: {e}")
        sys.exit(e.returncode)

    logger.info("OTEL collector started successfully")

    try:
        while True:
            time.sleep(3600)  # 1 hour at a time or gets interrupted due to SIGTERM.
    except KeyboardInterrupt:
        logger.info("Interrupted!")


if __name__ == "__main__":
    main()
