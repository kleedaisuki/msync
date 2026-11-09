#!/usr/bin/env bash
# Compatibility entrypoint; maintained infrastructure lives in typed Python.
exec "${PYTHON:-python3}" "$(dirname -- "$0")/check_local.py" "$@"
