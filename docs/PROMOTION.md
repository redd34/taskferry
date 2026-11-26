# Promotion and discoverability plan

How people find a library like this: package registries, GitHub search, and a handful of
community channels. The order below matters: announcing before the project is installable and
cross-platform spends the one launch on something most visitors cannot use.

Legend: ✅ done · 🟡 prepared, waiting on a decision or credential · ⬜ not started

## 1. Remove what blocks adoption

| # | Step | Status | Notes |
|---|---|---|---|
| 1.1 | CI on Linux, Windows, macOS | see [TESTING.md](TESTING.md) | `.github/workflows/ci.yml` runs `scripts/verify.sh` on all three |
| 1.2 | Crate metadata for crates.io | ✅ | `cargo publish --dry-run` passes for `tokio-dotnet-core` and `tokio-dotnet-macros` |
| 1.3 | NuGet packages for runtime and generator | see section 6 | `dotnet pack` into `artifacts/nuget/` |
| 1.4 | **Decide the name** | 🟡 owner decision | Repo is `rustify`, code is `tokio-dotnet`. Registry names are effectively permanent. See section 5 |
| 1.5 | Publish to crates.io and NuGet | 🟡 needs 1.4 + API tokens | Irreversible (crates.io versions cannot be deleted); not done without explicit go-ahead |

## 2. Make the repository findable

| # | Step | Status | Notes |
|---|---|---|---|
| 2.1 | Repo description and topics | ✅ | topics: rust, dotnet, csharp, ffi, interop, tokio, async, nativeaot, source-generator, bindings |
| 2.2 | README leads with search phrases | ✅ | "call async Rust from C#", Rust/.NET interop, `Task<T>`, `IAsyncEnumerable<T>` |
| 2.3 | "Not affiliated with Tokio/Microsoft" note | ✅ | README, License section |
| 2.4 | Runnable sample (`cargo build && dotnet run`) | see section 6 | `samples/QuickStart` |
| 2.5 | Social preview image | ⬜ | Set in GitHub repo settings (manual upload) |

## 3. Position against existing tools

| # | Step | Status | Notes |
|---|---|---|---|
| 3.1 | Research current state of csbindgen, UniFFI C#, Interoptopus, others | see section 7 | Every claim sourced |
| 3.2 | Fair comparison table in the README | see section 7 | Only verified claims |

## 4. Launch channels (do after 1.5)

Nothing in this section is posted automatically: these go out under your name, to communities
with their own rules, and should be timed for when someone can actually install the project.

| # | Channel | Status | Asset |
|---|---|---|---|
| 4.1 | Technical blog post | 🟡 draft | [blog/async-rust-to-dotnet.md](blog/async-rust-to-dotnet.md) |
| 4.2 | r/rust, r/dotnet, r/csharp | 🟡 drafts below | Post the article, not the bare repo |
| 4.3 | This Week in Rust | ⬜ | Submission process in section 7 |
| 4.4 | Hacker News "Show HN" | 🟡 draft below | Needs a working install path |
| 4.5 | awesome-rust / awesome-dotnet | ⬜ | Usually require a published, maintained package |
| 4.6 | Answer existing questions | ⬜ | Demand list in section 7; answer with a real solution, disclose authorship |
| 4.7 | Flagship use case: tokenizers for .NET | ⬜ | A concrete package built on the bridge gives non-interop people a reason to arrive |

### Draft: Reddit (r/rust)

> **Title:** Bridging async Rust to .NET Task/IAsyncEnumerable: the lifetime bugs I hit (and one the compiler didn't)
>
> I wrote a proc-macro + Roslyn source generator that exposes `async fn` and `impl Stream` from Rust as
> `Task<T>` and `IAsyncEnumerable<T>` in C#, with zero-copy buffers and cancellation. The write-up covers
> the parts that were hard: keeping pinned .NET memory alive exactly as long as the Rust future, making
> the completion callback fire exactly once, and a use-after-free where an unbounded `&*ptr` let the
> compiler infer `&'static T`. [link to post]
>
> Repo: https://github.com/redd34/rustify — experimental; feedback on the unsafe code very welcome.

### Draft: Reddit (r/dotnet, r/csharp)

> **Title:** Calling async Rust from C# as Task<T> / IAsyncEnumerable<T>, NativeAOT-compatible
>
> A source generator reads a manifest emitted by a Rust macro and generates SafeHandle-based classes:
> `await engine.EncodeAsync(memory, ct)` returns an `IMemoryOwner<T>` over Rust's buffer (no copy),
> `await foreach` pulls from a Rust stream with backpressure, and cancelling the token aborts the Rust
> future. The post explains the GCHandle/pinning/TaskCompletionSource design and why the generator
> cannot emit `[LibraryImport]`. [link to post]

### Draft: Show HN

> **Title:** Show HN: Call async Rust from C# as Task and IAsyncEnumerable, zero-copy
>
> First comment: what it is in two sentences, what is verified (link TESTING.md), what is not
> (link BUGS.md), and the most interesting bug (the `'static` inference use-after-free).

## 5. Name decision

Filled in from research; see section 7.

## 6. Engineering deliverables for this plan

Filled in as they land.

## 7. Research

Filled in from the research report.
