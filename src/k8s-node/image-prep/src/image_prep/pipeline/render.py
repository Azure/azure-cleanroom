# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""Template rendering for cloud-init files."""

import logging
from pathlib import Path

import yaml
from jinja2 import Environment, FileSystemLoader, StrictUndefined

from image_prep.models import ImagePrepVars

logger = logging.getLogger("image-prep")

TEMPLATES_DIR = Path(__file__).parent.parent / "templates"
SCRIPTS_DIR = Path(__file__).parent.parent / "scripts"


def load_vars(vars_path: Path) -> ImagePrepVars:
    """Load and validate variables from a YAML file."""
    with open(vars_path) as f:
        raw = yaml.safe_load(f) or {}
    return ImagePrepVars(**raw)


def render_template(
    template_name: str,
    output_path: Path,
    *,
    vars_model: ImagePrepVars | None = None,
    extra_vars: dict | None = None,
    templates_dir: Path | None = None,
) -> Path:
    """Render a single Jinja2 template and write it to output_path.

    Template variables come from vars_model (if provided) merged with
    extra_vars. extra_vars take precedence.
    """
    tpl_dir = templates_dir or TEMPLATES_DIR
    env = Environment(
        loader=FileSystemLoader(str(tpl_dir)),
        undefined=StrictUndefined,
        keep_trailing_newline=True,
    )

    tpl_vars: dict = {}
    if vars_model is not None:
        tpl_vars = vars_model.model_dump()
        tpl_vars["scripts"] = _load_scripts()
    if extra_vars:
        tpl_vars.update(extra_vars)

    template = env.get_template(template_name)
    content = template.render(tpl_vars)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(content)
    logger.info("Rendered %s -> %s", template_name, output_path)
    return output_path


def _load_scripts() -> dict[str, str]:
    """Load all scripts from the scripts directory as a name→content dict."""
    scripts: dict[str, str] = {}
    if SCRIPTS_DIR.is_dir():
        for script_path in sorted(SCRIPTS_DIR.glob("*.sh")) + sorted(
            SCRIPTS_DIR.glob("*.py")
        ):
            scripts[script_path.stem] = script_path.read_text()
    return scripts


def validate_cloud_init(path: Path) -> None:
    """Validate that rendered cloud-init output is valid YAML."""
    with open(path) as f:
        yaml.safe_load(f)
