# Bugs

Known defects and sharp edges. Fixed bugs are kept at the bottom as a record of what the
regression tests cover. Planned work lives in [OPEN_ITEMS.md](OPEN_ITEMS.md).

## Open

### B1 — Editor tooling can write a stale bindings manifest
`#[dotnet_export]` rebuilds `target/tokio-dotnet-bindings.json` from the exports seen by the
current compiler process. A long-lived proc-macro server (rust-analyzer) accumulates exports
across edits, so after renaming or deleting an exported method it can write a manifest that
still lists the old symbol. The C# build then fails, or `NativeMethods` throws
`TypeInitializationException` at first use because the export no longer exists.
**Workaround:** run `cargo build` (a fresh process writes a correct manifest).
**Likely fix:** resolve exports with `NativeLibrary.TryGetExport` and fail per method, and/or
have the generator read the cdylib's real export list.

### B2 — A never-disposed `IMemoryOwner<T>` leaks its Rust buffer
`RustMemoryManager<T>` deliberately has no finalizer: a `Span<T>` does not keep the manager
alive, so finalizing could free memory that is still being read (CA2015). Forgetting `Dispose`
therefore leaks native memory instead. `RustLibrary.LiveBuffers` exposes the live count.

### B3 — A dropped Rust task reports "cancelled" even when nobody cancelled
If a `CompletionGuard` is dropped without completing (task abort), .NET sees
`TaskCanceledException` with a default token. This is the specified behaviour, but it is
indistinguishable from a real cancellation. Not reachable today (the runtime never shuts down).

### B4 — Rust panics print to stderr and carry no stack trace
The default panic hook still prints `thread 'tokio-dotnet' panicked at ...` for every caught
panic, and `RustFfiException` contains only the panic message, not the location or backtrace.

### B5 — NuGet advisory on the NativeAOT compiler package
`Microsoft.DotNet.ILCompiler` 9.0.7 (pinned by .NET SDK 9.0.303) is flagged NU1903
(GHSA-p3jp-9v83-76w9). It is left as a visible warning. CI installs the current SDK and is not
affected. **Fix:** update the local .NET SDK.

### B6 — Only 64-bit layouts are asserted
The Rust/C# struct layout parity tests are compiled only for 64-bit targets. 32-bit is unverified.

### B7 — Overloads are rejected
Two Rust methods that map to the same C# member name (`new` + `create`, `foo` + sync
`foo_async`, or a method named like its struct) now fail the build with a clear `TDN001` error.
C# could express some of these as overloads; the generator does not try.

## Fixed

| Bug | Fix | Covered by |
|---|---|---|
| Use-after-free: a stream method could keep borrowing `&self` after `Dispose` | Shims borrow the handle through `borrow_handle` with a bounded lifetime; self-borrowing streams no longer compile | manual compile-fail probe (see W3) |
| A Rust parameter named `handle` was passed as the object pointer | Generator emits `this.handle` | `SyncCalls_RoundTrip` (`Echo`) |
| Cancelled stream ended silently instead of throwing | Cursor reports cancellation when the stream was dropped by `cancel` | `StreamCancellation_ThrowsAndDropsRustStream` |
| Stale manifest entries survived renames until `cargo clean` | Manifest is rebuilt from the current compilation only (see B1 for the remaining editor case) | — |
| Parameter names `ctx` / `out` / `cancellation_token` clashed with generated locals | Generator locals are prefixed `__tdn_`; the token parameter is renamed on clash | `CancellableCall_ThatCompletes_ReturnsNormally` (`EchoAsync`) |
| Rust parameters named `__out`, `__state`, ... clashed with shim locals | Parser rejects parameter names starting with `__` | — |
| Methods named `clone`, `to_string`, ... resolved to `Arc`'s method in async shims | Shims call `Type::method(&*this, ..)` | — |
| A panicking `Drop` in `free_*` / stream free / cancel aborted the process | Teardown runs under `guarded_drop` (catch_unwind + Tokio context) | — |
| A managed exception could escape the `[UnmanagedCallersOnly]` callback | `Complete` implementations catch and route to the task/enumerator | — |
| A disposed `RustMemoryManager` could still be pinned | `Pin` throws `ObjectDisposedException` | — |
