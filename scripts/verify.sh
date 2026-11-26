#!/usr/bin/env bash
# Full Rust + C# build, native artifact copy, tests, and the NativeAOT gate.
# SKIP_AOT=1 skips the (slow) NativeAOT publish.
set -euo pipefail
cd "$(dirname "$0")/.."

case "$(uname -s)" in
  Darwin) LIB=libtokio_dotnet_example.dylib; OS=osx ;;
  Linux) LIB=libtokio_dotnet_example.so; OS=linux ;;
  MINGW* | MSYS* | CYGWIN*) LIB=tokio_dotnet_example.dll; OS=win ;;
  *) echo "unsupported OS: $(uname -s)" >&2; exit 1 ;;
esac
case "$(uname -m)" in
  arm64 | aarch64) ARCH=arm64 ;;
  *) ARCH=x64 ;;
esac

echo "==> cargo build + test"
cargo build --release --workspace   # also emits target/tokio-dotnet-bindings.json
cargo test --release --workspace

echo "==> dotnet build + test"
dotnet build dotnet/TokioDotnet.slnx -c Release --nologo -v quiet
TEST_BIN=dotnet/TokioDotnet.Tests/bin/Release/net9.0
cp "target/release/$LIB" "$TEST_BIN/"
dotnet test dotnet/TokioDotnet.Tests -c Release --no-build --nologo

if [ "${SKIP_AOT:-0}" != 1 ]; then
  echo "==> NativeAOT publish + run"
  AOT_OUT=target/aot-smoke
  dotnet publish dotnet/TokioDotnet.AotSmoke -c Release -r "$OS-$ARCH" -o "$AOT_OUT" --nologo -v quiet
  cp "target/release/$LIB" "$AOT_OUT/"
  "$AOT_OUT/TokioDotnet.AotSmoke"
fi

echo "==> verify OK"
