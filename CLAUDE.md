# CLAUDE.md — Master Engineering Blueprint: `tokio-dotnet`

## 1. Executive Vision & Problem Statement
Build **`tokio-dotnet`**, an open-source, zero-copy, NativeAOT-compatible FFI bridge between **Async Rust (`Tokio` / `futures::Stream`)** and **Modern C# (.NET 8/9+ `Task<T>` / `IAsyncEnumerable<T>` / `Span<T>`)**.

### The Industry Gap
While `PyO3` + `maturin` solved Rust-to-Python interop, .NET teams adopting high-performance Rust engines (for vector search, cryptography, local LLM tokenization, or streaming parsers) are still forced to hand-roll fragile `GCHandle` + `TaskCompletionSource` state machines or pay heavy serialization penalties. `tokio-dotnet` eliminates this friction with a single `#[dotnet_export]` attribute in Rust and a C# Roslyn Incremental Source Generator (`TokioDotnet.Generator`) that emits safe, zero-allocation bindings at compile time.

---

## 2. Target Developer Experience (The "North Star" API)

### Rust Side (`crates/tokio-dotnet-example/src/lib.rs`)
```rust
use tokio_dotnet_core::{FfiSlice, RustResult};
use tokio_dotnet_macros::dotnet_export;
use futures::Stream;

#[dotnet_export]
pub struct TokenizerEngine {
    // Internal state safely owned on Rust heap
}

#[dotnet_export]
impl TokenizerEngine {
    pub fn new(vocab_json: &[u8]) -> RustResult<Self> { ... }

    /// Runs asynchronously on Tokio; never blocks a .NET ThreadPool thread.
    /// Input `text` is pinned zero-copy from C# `ReadOnlyMemory<byte>`.
    /// Output `Vec<u32>` is transferred zero-copy to C# as `Memory<uint>`.
    pub async fn encode_async(&self, text: &[u8]) -> RustResult<Vec<u32>> { ... }

    /// Streams tokens back to C# as `IAsyncEnumerable<uint>` with backpressure.
    pub fn stream_tokens(&self, prompt: String) -> impl Stream<Item = RustResult<u32>> { ... }
}
Generated C# Side (Consumed by .NET Developers)C#using var engine = TokenizerEngine.Create(vocabBytes.Span);

// 1. Non-blocking async call with zero-copy Memory<uint> return and CancellationToken support
using IMemoryOwner<uint> tokenOwner = await engine.EncodeAsync(inputMemory, cancellationToken);
ReadOnlySpan<uint> tokens = tokenOwner.Memory.Span;

// 2. Native async streaming with early-break cleanup
await foreach (uint token in engine.StreamTokens("Hello world", cancellationToken))
{
    Console.WriteLine(token);
}
3. Workspace ArchitectureMaintain this exact monorepo layout:Plaintexttokio-dotnet/
├── CLAUDE.md                                # This master specification
├── Cargo.toml                               # Rust workspace (resolver = "2")
├── crates/
│   ├── tokio-dotnet-core/                   # FFI ABI structs, Tokio runtime, PanicCatcher, GCHandle bridge
│   │   └── src/
│   │       ├── lib.rs
│   │       ├── runtime.rs                   # Global multi-thread Tokio runtime + CancellationToken registry
│   │       ├── memory.rs                    # FfiSlice<T>, FfiBytes, FfiString, DropCallback
│   │       ├── task_bridge.rs               # TaskCallback<T>, AtomicBool single-resolution guard
│   │       ├── stream_bridge.rs             # Pull-based async stream cursor for IAsyncEnumerable<T>
│   │       └── panic.rs                     # catch_unwind / FutureExt::catch_unwind -> FfiError
│   ├── tokio-dotnet-macros/                 # #[dotnet_export] proc-macro
│   │   └── src/
│   │       ├── lib.rs
│   │       ├── parser.rs                    # Parses structs, impl blocks, async fn, Stream return types
│   │       ├── codegen_rust.rs              # Generates extern "C" shims & C-ABI type conversions
│   │       └── codegen_meta.rs              # Emits deterministic JSON/binary FFI schema for C# generator
│   └── tokio-dotnet-example/                # Reference cdylib crate
│       └── src/
│           ├── lib.rs
│           ├── stress_ops.rs                # Deliberate panics, delays, large buffers, cancellations
│           └── hf_tokenizer.rs              # Real-world wrapper around Hugging Face `tokenizers` crate
├── dotnet/
│   ├── TokioDotnet.slnx                     # .NET solution file
│   ├── TokioDotnet.Runtime/                 # Core runtime library (net8.0;net9.0, AllowUnsafeBlocks=true)
│   │   ├── RustSafeHandle.cs                # SafeHandleZeroOrMinusOneIsInvalid base class
│   │   ├── RustMemoryManager.cs             # MemoryManager<T> wrapping Rust-owned FfiSlice<T>
│   │   ├── RustTaskBridge.cs                # GCHandle + TaskCompletionSource<T> + [UnmanagedCallersOnly]
│   │   ├── RustAsyncEnumerable.cs           # IAsyncEnumerable<T> / IAsyncEnumerator<T> cursor wrapper
│   │   ├── RustCancellationHandle.cs        # Cross-FFI CancellationToken registration bridge
│   │   └── RustFfiException.cs              # Marshaled Rust error & panic stack trace exception
│   ├── TokioDotnet.Generator/               # Roslyn IIncrementalGenerator (netstandard2.0)
│   │   └── BindingGenerator.cs              # Reads emitted FFI schema JSON and generates partial C# classes
│   └── TokioDotnet.Tests/                   # xUnit + BenchmarkDotNet verification suite
│       ├── ConcurrencyAndGcTests.cs         # 10k concurrent tasks + aggressive GC.Collect() torture tests
│       ├── ZeroCopyMemoryTests.cs           # Verifies pointer identity & destructor firing on Dispose()
│       ├── StreamingAndCancelTests.cs       # Early `await foreach` break & CancellationToken propagation
│       ├── PanicSafetyTests.cs              # Verifies Rust panic!() becomes RustFfiException cleanly
│       └── FfiBenchmarks.cs                 # BenchmarkDotNet: zero-copy Memory<T> vs byte[] P/Invoke
└── scripts/
    ├── verify.sh                            # Full Rust + C# build, artifact copy, and test runner
    └── bench.sh                             # Runs BenchmarkDotNet in Release mode
4. Exact C-ABI Data Structures & Memory ContractsEvery struct crossing the FFI boundary must use #[repr(C)] in Rust and [StructLayout(LayoutKind.Sequential)] in C#. Never pass Rust String, Vec<T>, &[T], Result<T, E>, Option<T>, or bool directly across extern "C".4.1 Primitive Mapping RulesRust TypeC-ABI RepresentationC# RepresentationNotesboolu8 (0 = false, 1 = true)byte (converted to bool)Avoids C# 4-byte BOOL vs Rust 1-byte bool UB&[T] (C# → Rust)FfiReadOnlySlice<T>FfiReadOnlySlice<T>Pinned via MemoryHandle until future completesVec<T> (Rust → C#)FfiOwnedSlice<T>RustMemoryManager<T>Zero-copy; Rust frees on SafeHandle.ReleaseHandle()String (Rust → C#)FfiOwnedSlice<u8>Encoding.UTF8.GetStringDecoded & immediately freed in C# callbackResult<T, E>FfiOutcome<T>TaskCompletionSource<T>Tagged union (is_ok: u8, value: T, err: FfiError)&OpaqueStruct*const c_voidRustSafeHandleReference-counted (Arc<T>) for async methods!4.2 Critical Lifetime Invariant: Arc<T> for Async Methods on Opaque StructsTRAP TO AVOID: If C# calls var task = engine.EncodeAsync(data); engine.Dispose(); await task;, a raw *const Engine pointer would dangle while Tokio is still executing!Mandatory Rule: Every #[dotnet_export] struct must internally wrap its state in Arc<InnerState>.Synchronous methods borrow &InnerState while C# holds DangerousAddRef on RustSafeHandle.async fn methods clone the Arc<InnerState> synchronously inside the extern "C" shim before spawning onto Tokio. That way, even if C# disposes the SafeHandle 1 nanosecond after starting the Task, the Rust struct stays alive until the Tokio task finishes, then drops cleanly.4.3 FfiOwnedSlice<T> (Zero-Copy Rust → C#)Rust#[repr(C)]
pub struct FfiOwnedSlice<T> {
    pub ptr: *mut T,
    pub len: usize,
    pub cap: usize,
    /// Type-erased destructor: `unsafe extern "C" fn(ptr: *mut T, len: usize, cap: usize)`
    pub drop_fn: unsafe extern "C" fn(*mut T, usize, usize),
}
In C#, RustMemoryManager<T> extends MemoryManager<T>, returns new Span<T>(slice.ptr, (int)slice.len) from GetSpan(), and invokes slice.drop_fn(slice.ptr, slice.len, slice.cap) inside Dispose(bool disposing).Zero heap allocations for buffer data on the .NET GC heap.4.4 Async Task<T> Completion State MachineC# Caller:Creates var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); (CRITICAL: RunContinuationsAsynchronously prevents Tokio worker threads from being hijacked by C# synchronous await continuations!)Pins input ReadOnlyMemory<T> arguments via MemoryHandle and stores them alongside tcs in a TaskCallContext<T> object.Allocates GCHandle handle = GCHandle.Alloc(context, GCHandleType.Normal);.Passes GCHandle.ToIntPtr(handle) and a static [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] callback pointer to Rust.Rust Shim:Wraps (state_ptr, callback_fn) in a CompletionGuard containing completed: AtomicBool.If the CompletionGuard is dropped without complete() being called (e.g., Tokio runtime shutdown or task abort), its Drop impl automatically invokes callback_fn(state_ptr, FfiOutcome::cancelled()) so C# never leaks a GCHandle or hangs a Task.C# Callback ([UnmanagedCallersOnly]):Reconstructs GCHandle.FromIntPtr(statePtr), extracts TaskCallContext<T>, disposes all pinned input MemoryHandles, calls handle.Free(), and resolves tcs.TrySetResult(...) or tcs.TrySetException(...).4.5 Pull-Based IAsyncEnumerable<T> Streaming ProtocolInstead of Rust pushing unconstrained callbacks (which causes unbounded memory growth if C# consumes slowly), implement a pull-based async cursor:Rust boxes the Pin<Box<dyn Stream<Item = RustResult<T>> + Send>> behind an opaque *mut FfiStreamCursor.C# RustAsyncEnumerator<T>.MoveNextAsync() calls extern "C" fn stream_poll_next(cursor_ptr, state_ptr, on_item_callback).Rust spawns cursor.next().await onto Tokio and resolves the single-item TaskCompletionSource<FfiStreamItem<T>> (HasItem(T) | EndOfStream | Error(FfiError)).When C# exits await foreach (normally, via exception, or via CancellationToken), DisposeAsync() calls extern "C" fn stream_free(cursor_ptr), immediately dropping the Rust stream and aborting upstream work.5. Phased Execution Plan & Acceptance CriteriaExecute sequentially. Run ./scripts/verify.sh at the end of every phase and verify 100% pass rate before moving to the next phase.Phase 1: Workspace Bootstrap & Core FFI RuntimeVerify toolchains (rustc --version, cargo --version, dotnet --version).Create the workspace structure (Cargo.toml, crates/*, dotnet/*, scripts/verify.sh).Implement tokio-dotnet-core:Global multi-threaded tokio::runtime::Runtime via OnceLock.FfiOwnedSlice<T>, FfiReadOnlySlice<T>, FfiError, FfiOutcome<T>.CompletionGuard with AtomicBool single-fire guarantee and Drop-based leak prevention.catch_unwind + FutureExt::catch_unwind panic wrapper.Implement TokioDotnet.Runtime (RustSafeHandle, RustMemoryManager<T>, RustTaskBridge, RustFfiException).Acceptance Gate: A hand-wired test function in tokio-dotnet-example passes a buffer from C# to Rust, transforms it asynchronously on Tokio, returns a zero-copy IMemoryOwner<byte>, and passes dotnet test.Phase 2: Procedural Macro (#[dotnet_export]) & C# Source GeneratorImplement tokio-dotnet-macros:Parse #[dotnet_export] on struct and impl blocks.Generate extern "C" constructors, destructors (free_<Struct>), sync methods, and async fn shims with automatic Arc<T> cloning.Emit a clean JSON metadata manifest (target/tokio-dotnet-bindings.json) during build describing every exported struct, method signature, and repr(C) layout.Implement TokioDotnet.Generator (Roslyn IIncrementalGenerator):Consume the JSON manifest as AdditionalFiles in .csproj and emit idiomatic, XML-documented C# partial classes with LibraryImport (NativeAOT-ready, zero runtime reflection).Wire C# CancellationToken to tokio_util::sync::CancellationToken so cancelling in C# aborts the in-flight Rust future immediately.Acceptance Gate: tokio-dotnet-example compiles with zero manual DllImport declarations in C#; dotnet publish -p:PublishAot=true succeeds cleanly.Phase 3: IAsyncEnumerable<T> Streaming SupportExtend #[dotnet_export] to support methods returning impl Stream<Item = RustResult<T>> + Send + 'static.Implement FfiStreamCursor in tokio-dotnet-core and RustAsyncEnumerable<T> in TokioDotnet.Runtime.Update TokioDotnet.Generator to emit IAsyncEnumerable<T> methods accepting [EnumeratorCancellation] CancellationToken cancellationToken = default.Acceptance Gate: C# await foreach consumes a 10,000-item async stream from Rust, and breaking out at item #5 triggers the Rust stream's Drop counter within 50ms.Phase 4: Real-World Showcase (hf_tokenizer) & Torture BenchmarksAdd Hugging Face tokenizers (with onig or fancy-regex pure-Rust backend so cross-platform linking is frictionless) to tokio-dotnet-example wrapped via #[dotnet_export].Build the full xUnit adversarial test suite in TokioDotnet.Tests:GC Compaction Torture Test: Spawn 10,000 concurrent EncodeAsync calls with randomized payloads while a dedicated background thread runs GC.Collect(2, GCCollectionMode.Aggressive, true, true); GC.WaitForPendingFinalizers(); continuously. Assert 0 crashes, 0 corrupted bytes, and 0 leaked GCHandles.Panic Resilience Test: Invoke an async fn that executes panic!("boom") mid-await; verify C# receives RustFfiException with "boom" in the message and subsequent calls continue normally.Mid-Flight Dispose Test: Dispose TokenizerEngine immediately after firing 500 async tasks; verify all 500 tasks complete cleanly without use-after-free.Build FfiBenchmarks.cs (BenchmarkDotNet) comparing 1MB buffer round-trips via tokio-dotnet zero-copy Memory<byte> vs. standard byte[] P/Invoke marshaling (measuring throughput and Allocated bytes).6. Autonomous Engineering & Debugging ProtocolVerify Every Change: Never assume FFI code compiles or runs cleanly. Run ./scripts/verify.sh after every module implementation.Platform-Aware Dynamic Library Handling: scripts/verify.sh must detect OS (Darwin -> lib*.dylib, Linux -> lib*.so, Windows -> *.dll) and copy the compiled artifact into dotnet/TokioDotnet.Tests/bin/... automatically.Zero-Tolerance for UB:Never use DllImport in generated C# code—always use [LibraryImport] (source-generated P/Invoke) for NativeAOT compatibility.Every unsafe block in Rust and C# must have a // SAFETY: comment proving pointer validity, alignment, and lifetime bounds.If a native crash (SIGSEGV / SIGABRT) occurs during dotnet test, immediately run cargo test and inspect struct alignment (size_of / offset_of parity assertions between Rust and C#) before writing more features.Progress Tracking: After completing each phase, update a ## Status checklist at the bottom of this file so session state stays accurate.Begin now by inspecting the environment (rustc --version, dotnet --version), creating scripts/verify.sh, and executing Phase 1.
---

### Optional: `.claude/settings.json` for Hands-Free Execution
To prevent Claude Code from stopping to ask permission every time it runs `cargo`, `dotnet`, or `./scripts/verify.sh`, create `.claude/settings.json` alongside `CLAUDE.md`:

```json
{
  "permissions": {
    "allow": [
      "Bash(cargo *)",
      "Bash(rustc *)",
      "Bash(dotnet *)",
      "Bash(./scripts/verify.sh)",
      "Bash(./scripts/bench.sh)",
      "Bash(mkdir *)",
      "Bash(cp *)",
      "Bash(ls *)"
    ]
  },
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "if [ -f Cargo.toml ]; then cargo check --workspace --quiet 2>&1 | head -n 30; fi"
          }
        ]
      }
    ]
  }
}
---

## Status

Last full `./scripts/verify.sh` pass: 2026-11-25 (macOS arm64, rustc 1.100, .NET SDK 9.0.303).

- [x] Phase 1 — core FFI runtime + `TokioDotnet.Runtime`; hand-wired gate: `ZeroCopyMemoryTests.HandWired_AsyncTransform_ReturnsZeroCopyMemoryOwner`
- [x] Phase 2 — `#[dotnet_export]` + `TokioDotnet.Generator` + cancellation; NativeAOT gate: `dotnet/TokioDotnet.AotSmoke` (published and run by `verify.sh`)
- [x] Phase 3 — `IAsyncEnumerable<T>` streaming; gate: `StreamingAndCancelTests.EarlyBreak_DropsRustStreamWithin50ms`
- [x] Phase 4 — `hf_tokenizer` showcase, torture tests, `FfiBenchmarks` (`./scripts/bench.sh`)

### Deviations from this spec (deliberate)

- **No `[LibraryImport]` in generated code.** A Roslyn source generator cannot feed another one, so emitted
  `[LibraryImport]` partials would never get implemented. Generated bindings call blittable
  `delegate* unmanaged[Cdecl]` function pointers resolved once via `NativeLibrary` (no `DllImport`, no marshalling
  stubs, NativeAOT-verified).
- **No `FfiStreamItem<T>`.** A stream pull resolves an `FfiOutcome<T>`; end-of-stream is error code 4.
- **Completion callbacks take `*const FfiOutcome<T>`** (type-erased pointer) so a single non-generic
  `[UnmanagedCallersOnly]` method serves every `T` (such methods cannot be generic).
- **C# mirrors `FfiOwnedSlice` / `FfiReadOnlySlice` are non-generic** (their layout does not depend on `T`).
- **`RustMemoryManager<T>` has no finalizer** (CA2015: a `Span<T>` does not keep it alive). Undisposed owners leak.
- **Extra project `dotnet/TokioDotnet.AotSmoke`** — the AOT gate needs an executable.
- **`TokenizerEngine` lives in `hf_tokenizer.rs`**, `StressOps` in `stress_ops.rs`; `lib.rs` holds the hand-wired export.

### Supported `#[dotnet_export]` surface

Params: integer/float primitives, `bool`, `&[T]`, `&str`, `String`, `Vec<T>` (T primitive). Returns: the same owned
types, `()`, `Self` (sync constructors), each optionally in `RustResult<_>`; `impl Stream<Item = RustResult<T>>`.
Methods take `&self` or no receiver. Stream methods cannot take borrowed params.

Open work items and known bugs are tracked in `docs/OPEN_ITEMS.md` and `docs/BUGS.md`.
