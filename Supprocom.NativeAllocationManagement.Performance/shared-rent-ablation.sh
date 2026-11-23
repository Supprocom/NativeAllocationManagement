#!/usr/bin/env bash
# NativePool<T>.Shared rent-cost ablation: build each variant, then run the rows.
#   shared-rent-ablation.sh build            one Release build per variant, in sequence
#   shared-rent-ablation.sh run [options]    the --shared-rent-ablation mode (see SharedRentAblation.cs)
#   shared-rent-ablation.sh all [options]    build, then run
# Variant builds define NAM_ABL_<NAME> through -p:NamAblation=<Name>; Default defines nothing.
# NAM_ABLATION_VARIANTS limits the variants built. NAM_RUNNER overrides the command prefix (default: heavy-slot nice -n 19).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="${NAM_ABLATION_ROOT:-artifacts/shared-rent-ablation}"
RUNNER="${NAM_RUNNER:-heavy-slot nice -n 19}"
VARIANTS="${NAM_ABLATION_VARIANTS:-Default SharedLookup NoValidation NoToken NoAccounting SmallPooled FlatSlab}"
PROJECT=Supprocom.NativeAllocationManagement.Performance
export DOTNET_SYSTEM_NET_DISABLEIPV6=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

build() {
  for v in $VARIANTS; do
    extra=()
    if [ "$v" != Default ]; then extra=("-p:NamAblation=$v"); fi
    $RUNNER timeout 40m dotnet build "$PROJECT" -c Release -m:1 -p:UseSharedCompilation=false \
      --no-incremental "${extra[@]}" -o "$ROOT/$v"
    dotnet build-server shutdown > /dev/null 2>&1 || true
  done
}

run() {
  $RUNNER dotnet "$ROOT/Default/$PROJECT.dll" --shared-rent-ablation --variants-root "$ROOT" "$@"
}

mode="${1:-all}"
shift || true
case "$mode" in
  build) build ;;
  run) run "$@" ;;
  all) build; run "$@" ;;
  *) echo "usage: $0 build|run|all [options]" >&2; exit 2 ;;
esac
