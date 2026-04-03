#!/bin/bash
set -xe
isort "$@" --profile black
black "$@" -t py311