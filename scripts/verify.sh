#!/usr/bin/env bash
# Full Rust + C# build, tests, and the NativeAOT gate.
# The native library is copied by dotnet/TokioDotnet.Generator/build/TokioDotnet.Generator.targets.
# SKIP_AOT=1 skips the (slow) NativeAOT publish.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> cargo build + test"
cargo build --release --workspace   # also emits target/tokio-dotnet-bindings.json
cargo test --release --workspace

echo "==> dotnet build + test"
dotnet build dotnet/TokioDotnet.slnx -c Release --nologo -v quiet
dotnet test dotnet/TokioDotnet.Tests -c Release --no-build --nologo

if [ "${SKIP_AOT:-0}" != 1 ]; then
  echo "==> NativeAOT publish + run"
  case "$(uname -s)" in
    Darwin) OS=osx ;;
    Linux) OS=linux ;;
    MINGW* | MSYS* | CYGWIN*) OS=win ;;
    *) echo "unsupported OS: $(uname -s)" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    arm64 | aarch64) ARCH=arm64 ;;
    *) ARCH=x64 ;;
  esac
  AOT_OUT=target/aot-smoke
  dotnet publish dotnet/TokioDotnet.AotSmoke -c Release -r "$OS-$ARCH" -o "$AOT_OUT" --nologo -v quiet
  "$AOT_OUT/TokioDotnet.AotSmoke"
fi

echo "==> verify OK"
