# Open work items

Status of the four phases and the deliberate deviations from the original spec are in the
`## Status` section of [CLAUDE.md](../CLAUDE.md). Known defects are in [BUGS.md](BUGS.md).

## Verification gaps

- **W1 — CI on Linux and Windows.** Everything has only been run on macOS arm64. `scripts/verify.sh`
  has Linux and Windows (Git Bash) branches that have never executed. Add a GitHub Actions matrix
  (macOS, Ubuntu, Windows) running `./scripts/verify.sh`.
- **W2 — Test on .NET 8.** `TokioDotnet.Runtime` targets `net8.0;net9.0`, but the tests and the AOT
  smoke test only run on `net9.0`.
- **W3 — Compile-fail tests for the macro.** Rejections (self-borrowing streams, `&mut self`,
  unsupported types, reserved parameter names) were checked by hand. Add `trybuild` tests.
- **W4 — Generator unit tests.** `TokioDotnet.Generator` is only tested end to end through the
  example crate. Add snapshot tests over hand-written manifests (static methods, unit-returning
  async, name clashes, keyword parameter names).
- **W5 — Tests without regression coverage.** Several fixes in BUGS.md ("Fixed" table, rows marked —)
  have no automated test.
- **W6 — Sanitizers.** Run the Rust tests under Miri and the .NET suite against an ASan build of
  the cdylib.

## Features

- **F1 — More types.** `Option<T>`, user `#[repr(C)]` structs, nested exported structs as
  parameters/returns, `Vec<String>`, `&mut self` (via a lock), async constructors.
- **F2 — Free functions.** `#[dotnet_export]` only accepts structs and inherent impl blocks.
- **F3 — Backtraces.** Capture the panic location and backtrace into `RustFfiException` (see B4).
- **F4 — Configurable namespace and runtime.** The C# namespace is derived from the cdylib name;
  the Tokio runtime is a fixed global multi-thread runtime with default settings.
- **F5 — Batched stream pulls.** One native round trip per item keeps backpressure simple but
  costs a task hop per element; a `next_batch(n)` pull would cut that for fast streams.
- **F6 — Stream item ownership.** Streams of `Vec<T>` hand the consumer one `IMemoryOwner<T>` per
  item that it must dispose; nothing enforces this.

## Packaging

- **P1 — NuGet packages.** Ship `TokioDotnet.Runtime` and `TokioDotnet.Generator` as packages, with
  an MSBuild target that adds the manifest to `AdditionalFiles` and copies the native library
  (today each consumer `.csproj` does this by hand and `verify.sh` copies the cdylib).
- **P2 — crates.io.** Publish `tokio-dotnet-core` and `tokio-dotnet-macros`; add crate metadata.
- **P3 — README and LICENSE files.** The workspace declares `MIT OR Apache-2.0` but ships no
  license texts or user-facing README.
- **P4 — `tokenizers` 1.0.** The example pins `tokenizers` 0.21; a 1.0 release candidate exists.
- **P5 — Benchmark baseline.** `FfiBenchmarks.MarshalledByteArray` simulates copy-in/copy-out
  marshalling rather than using a real marshalled P/Invoke.
