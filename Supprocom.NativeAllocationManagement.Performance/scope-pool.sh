#!/usr/bin/env bash
# Scope pool benchmark: build the Performance project in Release, then run the --scope-pool mode.
#   scope-pool.sh build            one Release build
#   scope-pool.sh run [options]    the --scope-pool mode (see ScopePoolBenchmark.cs), e.g. --quick
#   scope-pool.sh all [options]    build, then run
# NAM_RUNNER overrides the command prefix (default: heavy-slot nice -n 19).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="${NAM_SCOPE_POOL_ROOT:-/home/claude/nam-wt/scope-pool-bench}"
RUNNER="${NAM_RUNNER:-heavy-slot nice -n 19}"
PROJECT=Supprocom.NativeAllocationManagement.Performance
export DOTNET_SYSTEM_NET_DISABLEIPV6=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

build() {
  $RUNNER timeout 40m dotnet build "$PROJECT" -c Release -m:1 -p:UseSharedCompilation=false \
    --no-incremental -o "$ROOT"
  dotnet build-server shutdown > /dev/null 2>&1 || true
}

run() {
  $RUNNER dotnet "$ROOT/$PROJECT.dll" --scope-pool "$@"
}

mode="${1:-all}"
shift || true
case "$mode" in
  build) build ;;
  run) run "$@" ;;
  all) build; run "$@" ;;
  *) echo "usage: $0 build|run|all [options]" >&2; exit 2 ;;
esac
