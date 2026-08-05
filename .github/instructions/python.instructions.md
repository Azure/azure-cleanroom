---
applyTo: "**/*.py"
---

# Python Conventions (azure-cleanroom)

## Package Management
- Uses **uv** workspace manager. Members defined in root `pyproject.toml`.
- Add new packages with `uv workspace add`.
- Setup: `uv venv && source .venv/bin/activate && uv lock && uv sync --all-packages`

## Formatting
- **ruff** for formatting and linting.
- Max line width: 88 characters (ruff default).

## Testing
- **unittest** (standard library), NOT pytest.
- Run: `python -m unittest src/code-launcher/test/test_code_launch.py`
- Run specific: `python -m unittest src/code-launcher/test/test_code_launch.TestCodeLauncherMain.test_method_name`

## CLI Entry Points
- Defined via `[project.scripts]` in each component's `pyproject.toml`.

## Auto-Generated Code
- Do NOT edit files in `src/sdk/` or `vendored_sdks/` — they are generated from TypeSpec/OpenAPI specs.

## Lint Checks (CI)
```bash
ruff check .
ruff format --check .
```
