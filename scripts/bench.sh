#!/usr/bin/env bash
# Runs the BenchmarkDotNet suite in Release mode. Extra args go to BenchmarkDotNet.
set -euo pipefail
cd "$(dirname "$0")/.."

cargo build --release --workspace
dotnet build dotnet/TokioDotnet.Tests -c Release --nologo -v quiet   # also copies the native library
dotnet dotnet/TokioDotnet.Tests/bin/Release/net9.0/TokioDotnet.Tests.dll --filter '*' "$@"
