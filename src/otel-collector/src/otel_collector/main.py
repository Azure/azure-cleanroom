import argparse
import base64
import json
import logging
import os
import signal
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Dict

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

    spark_metrics_endpoint: str = Field(
        default=os.getenv("SPARK_METRICS_ENDPOINT") or ""
    )

    prometheus_scrape_targets_base64: str = Field(
        default=os.getenv("PROMETHEUS_SCRAPE_TARGETS") or ""
    )

    resource_attributes_base64: str = Field(
        default=os.getenv("RESOURCE_ATTRIBUTES") or ""
    )

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
    def apache_spark_enabled(self) -> bool:
        return self.spark_metrics_endpoint != ""

    @property
    def prometheus_scrape_enabled(self) -> bool:
        return len(self.prometheus_scrape_configs) > 0

    @property
    def prometheus_scrape_configs(self) -> list:
        """Parse base64-encoded JSON list of scrape target configs.

        Each entry: {job_name, target, metrics_path}.
        """
        if not self.prometheus_scrape_targets_base64:
            return []
        decoded = json.loads(
            base64.b64decode(self.prometheus_scrape_targets_base64).decode("utf-8")
        )
        if not isinstance(decoded, list):
            raise ValueError(
                "PROMETHEUS_SCRAPE_TARGETS must be a JSON array, "
                f"got {type(decoded).__name__}"
            )
        required_keys = {"job_name", "target", "metrics_path"}
        for i, entry in enumerate(decoded):
            if not isinstance(entry, dict):
                raise ValueError(
                    f"PROMETHEUS_SCRAPE_TARGETS[{i}] must be an object, "
                    f"got {type(entry).__name__}"
                )
            missing = required_keys - entry.keys()
            if missing:
                raise ValueError(
                    f"PROMETHEUS_SCRAPE_TARGETS[{i}] missing keys: "
                    f"{', '.join(sorted(missing))}"
                )
        return decoded

    @property
    def resource_processor_enabled(self) -> bool:
        return bool(self.resolved_resource_attributes)

    @property
    def resolved_resource_attributes(self) -> dict:
        """Merge base64-encoded resource attributes with Kubernetes
        downward API env vars (POD_NAME, POD_NAMESPACE, NODE_NAME)."""
        attrs: dict[str, str] = {}
        if self.resource_attributes_base64:
            attrs.update(
                json.loads(
                    base64.b64decode(self.resource_attributes_base64).decode("utf-8")
                )
            )
        for env_key, attr_key in (
            ("POD_NAME", "k8s.pod.name"),
            ("POD_NAMESPACE", "k8s.namespace.name"),
            ("NODE_NAME", "k8s.node.name"),
        ):
            val = os.getenv(env_key, "")
            if val:
                attrs[attr_key] = val
        return attrs

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
                "apache_spark_enabled": config.apache_spark_enabled,
                "spark_metrics_endpoint": config.spark_metrics_endpoint,
                "prometheus_scrape_enabled": config.prometheus_scrape_enabled,
                "prometheus_scrape_configs": config.prometheus_scrape_configs,
                "resource_processor_enabled": config.resource_processor_enabled,
                "resource_attributes": config.resolved_resource_attributes,
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
