# Calling async Rust from C# without lying to either runtime

*Draft - not yet published.*

Suppose you have a Rust library built on Tokio and you want to call it from .NET. The synchronous part is easy: `extern "C"`, a blittable struct or two, done. The async part is where it gets hard. You want `await engine.EncodeAsync(bytes)` to give you a real `Task<T>`. You want the input buffer to go to Rust without being copied, and the `Vec<u32>` that comes back to reach C# without being copied either. And you don't want to hand-write a `GCHandle` + `TaskCompletionSource` state machine for every method.

tokio-dotnet is an experimental attempt at that. You put a `#[dotnet_export]` attribute on a Rust struct and its impl block. The proc-macro generates `extern "C"` shims and writes a JSON manifest, and a Roslyn source generator reads the manifest and emits the C# classes. This post is mostly about the places where that bridge nearly went wrong, including one use-after-free that compiled without a warning.

## What it looks like

This is the Rust side of the tokenizer example (`crates/tokio-dotnet-example/src/hf_tokenizer.rs`, trimmed):

```rust
#[dotnet_export]
pub struct TokenizerEngine {
    tokenizer: Tokenizer,
}

#[dotnet_export]
impl TokenizerEngine {
    pub fn new(vocab_json: &[u8]) -> RustResult<Self> {
        Ok(Self { tokenizer: Tokenizer::from_bytes(vocab_json)? })
    }

    pub async fn encode_async(&self, text: &[u8]) -> RustResult<Vec<u32>> {
        self.ids(std::str::from_utf8(text)?)
    }

    pub fn stream_tokens(&self, prompt: String) -> impl Stream<Item = RustResult<u32>> {
        stream::iter(match self.ids(&prompt) {
            Ok(ids) => ids.into_iter().map(Ok).collect(),
            Err(e) => vec![Err(e)],
        })
    }
}
```

And this is the C# that uses the generated bindings:

```csharp
using var engine = TokenizerEngine.Create(vocabBytes);

using IMemoryOwner<uint> tokens = await engine.EncodeAsync(inputMemory, cancellationToken);
ReadOnlySpan<uint> ids = tokens.Memory.Span;       // Rust's buffer, not a copy

await foreach (uint token in engine.StreamTokens("Hello world", cancellationToken))
    Console.WriteLine(token);                       // breaking out drops the Rust stream
```

`new` becomes a static `Create`. The async method takes a `ReadOnlyMemory<byte>`, which stays pinned until the call finishes, and returns `Task<IMemoryOwner<uint>>`, which wraps the Rust `Vec` directly. The stream becomes an `IAsyncEnumerable<uint>`. A `RustResult::Err` or a panic becomes a `RustFfiException`, and cancelling the token drops the Rust future instead of just abandoning the `Task`.

Under the hood, the C# side calls one generated shim per method. That shim spawns onto a global multi-threaded Tokio runtime and returns right away. When the future finishes, Rust calls back into .NET exactly once with the outcome.

## Lifetime trap one: Dispose right after the call

The first problem comes from very ordinary C#:

```csharp
var task = engine.EncodeAsync(data);
engine.Dispose();
await task;
```

The Rust object behind `engine` is an `Arc::into_raw(Arc<TokenizerEngine>)`. `Dispose` releases the `SafeHandle`, which calls `free_TokenizerEngine`, which drops .NET's single strong reference. If the spawned future only held a raw `&TokenizerEngine`, it would now be reading freed memory.

The generated async shim prevents this by taking its own strong count synchronously, before anything is spawned (`codegen_rust.rs`):

```rust
// SAFETY: .NET holds a SafeHandle reference for the duration of this
// synchronous shim, so the Arc is alive; taking our own strong count
// here keeps the state alive until the Tokio task finishes even if
// .NET disposes the handle right after this call returns.
let __this = unsafe {
    ::std::sync::Arc::increment_strong_count(__this as *const #ty);
    ::std::sync::Arc::from_raw(__this as *const #ty)
};
```

On the C# side, the generated method wraps the native call in `DangerousAddRef` / `DangerousRelease`, so the handle can't be released while the shim is running. When the shim returns, the spawned task owns a clone. `MidFlightDispose_500TasksStillComplete` starts 500 `EncodeAsync` calls, disposes the engine, and checks that all 500 results are correct.

## Lifetime trap two: pinned .NET memory inside a Rust future

The input is the harder case. `text: &[u8]` points into a .NET array that has been pinned with `Memory<T>.Pin()`. The C# completion handler unpins it. That means the Rust future, which holds the borrow, must be dropped **before** the callback fires, on every path: success, error, panic, cancellation, and the task being dropped without finishing.

The hand-wired Phase 1 export in `lib.rs` spells out the lie the shim has to tell the type system:

```rust
// SAFETY: .NET keeps `data` pinned until the callback, which only fires
// after the future below (the sole user of `data`) has been dropped.
let data: &'static [u8] = unsafe { data.as_slice() };
```

The slice isn't `'static`. It's valid until the callback. Two pieces in `task_bridge.rs` make that difference safe. The first is `CompletionGuard`, which fires exactly once:

```rust
fn fire(&self, outcome: FfiOutcome<T>) {
    if !self.completed.swap(true, Ordering::SeqCst) {
        unsafe { (self.callback)(self.state, &outcome as *const FfiOutcome<T> as *const c_void) }
    }
}

impl<T> Drop for CompletionGuard<T> {
    fn drop(&mut self) {
        if !self.completed.load(Ordering::SeqCst) {
            self.fire(FfiOutcome::cancelled());
        }
    }
}
```

If the guard is dropped without completing, .NET still gets a cancelled outcome, so it never leaks the `GCHandle` or leaves a `Task` hanging forever. The second piece is the wrapper that actually gets spawned:

```rust
/// Future + guard with a fixed drop order: the future (which may borrow
/// .NET-pinned memory) is always dropped *before* the callback unpins it.
struct Bridged<R: IntoFfi, F> {
    fut: Option<Pin<Box<F>>>,
    guard: Option<CompletionGuard<R::Ffi>>,
}
```

On the normal path, `poll` sets `this.fut = None` before it calls `guard.complete(...)`. If Tokio drops the task without finishing it, Rust drops struct fields in declaration order: `fut` goes first, then `guard`, and the guard's `Drop` fires the cancelled callback. Either way, the borrow is gone before anyone unpins. Cancellation uses the same path. `spawn_task` races the work against a `tokio_util` `CancellationToken` in a `biased` `tokio::select!`, and the losing branch is dropped when the select resolves, before the outer future returns. Panics are caught with `FutureExt::catch_unwind` inside the future, so they become an error outcome and never unwind across `extern "C"`.

## One callback for every `T`

The callback is a C# method marked `[UnmanagedCallersOnly]`, which is how you get a function pointer that native code can call without delegate marshalling. Such methods can't be generic, and they can't be declared inside a generic type. But the outcome type changes from method to method: `FfiOutcome<FfiOwnedSlice>` for a `Vec`, `FfiOutcome<ulong>` for a `u64`, and so on.

So the Rust callback signature erases the type:

```rust
pub type CompletionCallback = unsafe extern "C" fn(state: *mut c_void, outcome: *const c_void);
```

That lets a single static C# method serve every task and every stream pull:

```csharp
[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
private static void OnComplete(nint state, void* outcome)
{
    GCHandle handle = GCHandle.FromIntPtr(state);
    var context = (RustCallContext)handle.Target!;
    handle.Free();
    Interlocked.Decrement(ref RustCallContext.s_liveHandles);
    context.Complete(outcome);
}
```

The type comes back through the `GCHandle` target. `RustTaskContext<TNative, T, TConv>` knows its own `TNative`, so its `Complete` override casts `void*` to `FfiOutcome<TNative>*` and copies the value out while the pointer is still valid. Rust's outcome lives on the stack of `fire`, so it's only valid for the duration of the call. `Complete` also catches everything, because a managed exception must never escape into Rust.

`OnComplete` runs on a Tokio worker thread, which is why the `TaskCompletionSource` is created with `TaskCreationOptions.RunContinuationsAsynchronously`. Without it, `TrySetResult` would run the caller's `await` continuation inline on that worker, and arbitrary .NET code would end up blocking the Tokio runtime. `Continuations_DoNotRunOnTokioWorkers` checks this.

## Streams are pulled, not pushed

A Rust stream could push every item into a .NET callback as soon as it's produced. Then a slow consumer means unbounded buffering on the .NET side. tokio-dotnet uses a pull cursor instead: each `MoveNextAsync` calls `tokio_dotnet_stream_next` once, Rust spawns one `stream.next()`, and the result comes back through the same `OnComplete` callback. The stream can't produce item N+1 until the consumer asks for it, so backpressure is built in.

On the C# side, `RustAsyncEnumerator` is itself the call context, and it implements `IValueTaskSource<bool>` over a reused `ManualResetValueTaskSourceCore<bool>`. That way a pull doesn't allocate a new `Task`:

```csharp
public ValueTask<bool> MoveNextAsync()
{
    _source.Reset();
    _cursor.Next(this);
    return new ValueTask<bool>(this, _source.Version);
}
```

When the consumer leaves `await foreach` early, `DisposeAsync` releases the cursor's `SafeHandle`, and `tokio_dotnet_stream_free` cancels and drops the stream right away. The exception is when a pull is still in flight: that pull holds the stream's lock, sees the cancelled token, and drops the stream itself. `EarlyBreak_DropsRustStreamWithin50ms` breaks at item 5 and requires a `Drop` counter inside the Rust stream to increment within 50 ms. In the last recorded run, the whole test took 4 ms.

The cost is one native round trip and one task hop per item. For fast streams that adds up, and batched pulls are listed as open work (F5).

## The bug that compiled cleanly

This is the part I'd want to read if I were you.

Synchronous shims borrow the object for the duration of the call. Before an independent review of the unsafe code, they did that by dereferencing the raw handle directly, the equivalent of `let __this = unsafe { &*(__this as *const T) };`. For a sync method returning an owned value, that's fine. Stream methods are synchronous too, though. They return a stream that outlives the call, and their shim hands that stream straight to the cursor:

```rust
quote!(::core::result::Result::Ok(#core::stream_bridge::FfiStreamCursor::new(#call)))
```

where `#call` expands to `TokenizerEngine::stream_tokens(&*__this, prompt)`, and `FfiStreamCursor::new` requires `S: Stream<..> + Send + 'static`.

Here's the inference. Dereferencing a raw pointer produces a reference with an *unbounded* lifetime: nothing ties it to the pointer, a local, or the function body. The borrow checker is free to pick whatever lifetime satisfies the constraints. Now suppose a user writes a stream that really does borrow `self`. The workspace is on edition 2021, so that means adding `+ '_` to the return type (this example is hypothetical, not from the repo):

```rust
pub fn bytes(&self) -> impl Stream<Item = RustResult<u8>> + '_ { /* reads self */ }
```

The `'static` bound on `FfiStreamCursor::new` requires that `'_` outlive `'static`. The unbounded lifetime from `&*ptr` can satisfy that, so the compiler quietly picks `&'static TokenizerEngine`. No error, no warning. The stream is boxed into a cursor that .NET can keep using after `engine.Dispose()` has dropped the last `Arc`. That's a use-after-free, and the only unsafe code involved was a raw-pointer deref with a `// SAFETY:` comment that was true for every case except this one.

The fix is to put an upper bound on the lifetime (`memory.rs`):

```rust
/// Borrows the object behind an exported handle for no longer than `_scope`.
/// The explicit lifetime stops a shim from inferring `&'static T`, which would
/// let e.g. a returned stream keep borrowing the object after .NET frees it.
pub unsafe fn borrow_handle<'a, T>(_scope: &'a (), handle: *const c_void) -> &'a T {
    unsafe { &*(handle as *const T) }
}
```

and to borrow through a stack local in the shim:

```rust
let __scope = ();
let __this = unsafe { #core::memory::borrow_handle::<#ty>(&__scope, __this) };
```

Now `'a` can't outlive `__scope`, which dies at the end of the shim's closure, so `'_: 'static` can't be satisfied and the self-borrowing stream fails to compile. Streams that don't borrow `self` are unaffected. `stream_tokens` above collects its ids eagerly, and on edition 2021 its plain `impl Stream` doesn't capture `&self`'s lifetime at all.

The async shim has the same unbounded-lifetime pattern on purpose. Its `as_slice()` calls on pinned inputs also get inferred as `'static`, because `spawn_task` requires `F: 'static`. The difference is that `Bridged`'s drop order backs up that lifetime. Nothing backed it up in the sync shim. To be honest about coverage: the compile-fail check is a manual probe. There is no `trybuild` test for it yet (W3).

## A Roslyn gotcha: generators don't feed generators

The original plan was for the generator to emit `[LibraryImport]` partial methods, the NativeAOT-friendly replacement for `DllImport`. It doesn't work. `[LibraryImport]` is implemented by another source generator, and one source generator's output isn't visible to the others, so the emitted partials are never implemented and the build fails.

The generator emits blittable unmanaged function pointers instead, resolved once per library through `NativeLibrary`:

```csharp
internal static readonly delegate* unmanaged[Cdecl]<nint, FfiReadOnlySlice, nint, nint,
    delegate* unmanaged[Cdecl]<nint, void*, void>, void> TokenizerEngine_encode_async
    = (...)Library.GetExport("TokenizerEngine_encode_async");
```

(That is the shape of the generated field; the generator builds it in `NativeField`.) Every type in these signatures is blittable, so no marshalling stubs are needed and there's no reflection. `verify.sh` publishes a smoke-test app with `PublishAot=true` and runs the resulting native binary.

One downside: binding by symbol name at type initialization is how open bug B1 shows up. A long-lived rust-analyzer process can write a stale manifest that still lists a deleted export, and then `NativeMethods` throws `TypeInitializationException` the first time it's used. The workaround is a fresh `cargo build`.

<!-- TODO: comparison section. Use the table in README.md ("How it compares"); do not claim to be first. -->

## Numbers and limits

The last full run, on 2026-11-26, passed 26 of 26 checks: 3 Rust unit tests, 22 xUnit tests and the NativeAOT smoke test. That was on **macOS arm64 only**, with rustc 1.100.0 and .NET SDK 9.0.303, with tests on `net9.0`. Linux, Windows, .NET 8 and 32-bit targets haven't been run. `verify.sh` has Linux and Windows branches that have never executed, and the layout parity assertions only exist for 64-bit.

This is the 1 MB buffer round trip benchmark:

| Method | Mean | StdDev | Ratio | Allocated |
|---|---:|---:|---:|---:|
| `MarshalledByteArray` (baseline) | 165.00 µs | 1.34 µs | 1.00 | 1,049,740 B |
| `ZeroCopySync` | 28.77 µs | 0.03 µs | 0.17 | 56 B |
| `ZeroCopyAsync` | 46.43 µs | 0.17 µs | 0.28 | 460 B |

Read it with three caveats. It's a BenchmarkDotNet short run (3 iterations), so treat it as indicative. The baseline *simulates* classic `byte[]` marshalling (copy to native, call, copy back) rather than going through a real marshalled P/Invoke. And it was run on 2026-11-25, before the last round of fixes, and hasn't been re-run since. What it does show is the expected shape: the copies dominate at 1 MB, and the Tokio hop costs about 18 µs on top of the sync path.

Some limits matter for real use:

- **`RustMemoryManager<T>` has no finalizer.** A `Span<T>` doesn't keep its `MemoryManager` alive, so a finalizer could free Rust memory while a span over it is still being read (CA2015). Forgetting to `Dispose` an `IMemoryOwner<T>` therefore leaks native memory instead of risking corruption (B2). `RustLibrary.LiveBuffers` reports the live count.
- **Dropped tasks look cancelled (B3).** A `CompletionGuard` dropped without completing surfaces as `TaskCanceledException` with a default token. You can't tell it apart from real cancellation, although the docs note it isn't reachable today because the runtime never shuts down.
- **Panics print to stderr and carry no backtrace (B4).** `RustFfiException` contains only the panic message.
- **Overloads are rejected (B7).** Two Rust methods that map to the same C# name fail the build with `TDN001`.
- **The supported surface is narrow:** numeric primitives, `bool`, `&[T]`, `&str`, `String`, `Vec<T>` of primitives, `Self` constructors, `RustResult<_>`, and `impl Stream<Item = RustResult<T>>`. Methods take `&self` or no receiver. There's no `Option<T>`, no user structs, no `&mut self`, and no free functions yet.
- The generator has no unit tests of its own, several fixed bugs have no regression test, and nothing has been run under Miri or ASan yet (W4-W6).

## Try it

The code is at https://github.com/redd34/rustify. **No packages are published yet**, neither on crates.io nor on NuGet. To use it today, you depend on the two crates by path, build a `cdylib`, reference `TokioDotnet.Runtime` and add `TokioDotnet.Generator` as an analyzer, add `target/tokio-dotnet-bindings.json` as `AdditionalFiles`, and copy the native library next to your output. `TokioDotnet.Tests.csproj` is a working template. `./scripts/verify.sh` runs the whole pipeline, and `docs/BUGS.md` and `docs/OPEN_ITEMS.md` list what's known to be missing or broken. Bug reports from Linux and Windows would be especially useful, since nobody has run it there.
