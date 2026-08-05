#!/bin/bash
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# PostToolUse hook: runs dotnet format on C# files after agent edits.

set -e

# Read hook input from stdin.
INPUT=$(cat)

# Extract the file path from tool_input.filePath (VS Code camelCase convention).
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.filePath // empty')

if [ -z "$FILE_PATH" ]; then
    exit 0
fi

# Only act on C# files.
if [[ "$FILE_PATH" != *.cs ]]; then
    exit 0
fi

if [ ! -f "$FILE_PATH" ]; then
    exit 0
fi

dotnet format whitespace --include "$FILE_PATH" 2>/dev/null || true
dotnet format style --include "$FILE_PATH" --severity warning 2>/dev/null || true
