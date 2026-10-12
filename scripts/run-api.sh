#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
export LIBRARY_CONFIG="$repo_root/.local/config.json"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$repo_root"
exec "${DOTNET:-dotnet}" run --project server/Library.Api --no-launch-profile --urls http://127.0.0.1:5080
