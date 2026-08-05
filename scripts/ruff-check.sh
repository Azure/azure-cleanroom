#!/bin/bash
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Shared ruff format and lint script.
# Usage:
#   scripts/ruff-check.sh              # Check mode (CI) - all files.
#   scripts/ruff-check.sh --fix <file> # Fix mode (hooks) - specific file.

set -e

FIX=0
FILES=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --fix)
            FIX=1
            shift
            ;;
        *)
            FILES="$FILES $1"
            shift
            ;;
    esac
done

if [ $FIX -ne 0 ]; then
    uv run ruff format $FILES
    uv run ruff check --fix $FILES
else
    uv run ruff format --check $FILES
    uv run ruff check $FILES
fi
