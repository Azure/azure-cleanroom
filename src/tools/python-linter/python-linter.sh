#!/bin/bash
set -xe
ruff check --select I --fix "$@"
ruff format "$@"