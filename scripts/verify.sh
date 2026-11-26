#!/usr/bin/env bash
# Full Rust + C# build, tests, and the NativeAOT gate.
# The native library is copied by dotnet/Taskferry.Generator/build/Taskferry.Generator.targets.
# SKIP_AOT=1 skips the (slow) NativeAOT publish.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> cargo build + test"
cargo build --release --workspace   # also emits target/taskferry-bindings.json
cargo test --release --workspace

echo "==> dotnet build + test"
dotnet build dotnet/Taskferry.slnx -c Release --nologo -v quiet
dotnet test dotnet/Taskferry.Tests -c Release --no-build --nologo

echo "==> quick-start sample"
dotnet run --project samples/QuickStart -c Release --no-build

if [ "${SKIP_AOT:-0}" != 1 ]; then
  echo "==> NativeAOT publish + run"
  # Not named OS: on Windows that would overwrite the OS=Windows_NT variable MSBuild relies on.
  case "$(uname -s)" in
    Darwin) RID_OS=osx ;;
    Linux) RID_OS=linux ;;
    MINGW* | MSYS* | CYGWIN*) RID_OS=win ;;
    *) echo "unsupported OS: $(uname -s)" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    arm64 | aarch64) ARCH=arm64 ;;
    *) ARCH=x64 ;;
  esac
  AOT_OUT=target/aot-smoke
  dotnet publish dotnet/Taskferry.AotSmoke -c Release -r "$RID_OS-$ARCH" -o "$AOT_OUT" --nologo -v quiet
  "$AOT_OUT/Taskferry.AotSmoke"
fi

echo "==> verify OK"
