#!/usr/bin/env bash
# Runs the BenchmarkDotNet suite in Release mode. Extra args go to BenchmarkDotNet.
set -euo pipefail
cd "$(dirname "$0")/.."

case "$(uname -s)" in
  Darwin) LIB=libtokio_dotnet_example.dylib ;;
  Linux) LIB=libtokio_dotnet_example.so ;;
  *) LIB=tokio_dotnet_example.dll ;;
esac

cargo build --release --workspace
dotnet build dotnet/TokioDotnet.Tests -c Release --nologo -v quiet
BIN=dotnet/TokioDotnet.Tests/bin/Release/net9.0
cp "target/release/$LIB" "$BIN/"
dotnet "$BIN/TokioDotnet.Tests.dll" --filter '*' "$@"
