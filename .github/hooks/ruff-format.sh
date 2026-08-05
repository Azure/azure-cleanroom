#!/bin/bash
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# PostToolUse hook: runs ruff format and lint on Python files after agent edits.

set -e

# Read hook input from stdin.
INPUT=$(cat)

# Extract the file path from tool_input.filePath (VS Code camelCase convention).
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.filePath // empty')

if [ -z "$FILE_PATH" ]; then
    exit 0
fi

# Only act on Python files.
if [[ "$FILE_PATH" != *.py ]]; then
    exit 0
fi

if [ ! -f "$FILE_PATH" ]; then
    exit 0
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
ROOT_DIR=$(cd "$SCRIPT_DIR/../.." && pwd)

"$ROOT_DIR/scripts/ruff-check.sh" --fix "$FILE_PATH"
