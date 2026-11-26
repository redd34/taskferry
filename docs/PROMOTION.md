# Promotion and discoverability plan

How people find a library like this: package registries, GitHub search, and a handful of
community channels. The order below matters: announcing before the project is installable and
cross-platform spends the one launch on something most visitors cannot use.

Legend: ✅ done · 🟡 prepared, waiting on a decision or credential · ⬜ not started

## 1. Remove what blocks adoption

| # | Step | Status | Notes |
|---|---|---|---|
| 1.1 | CI on Linux, Windows, macOS | ✅ | `.github/workflows/ci.yml` runs `scripts/verify.sh` on all three |
| 1.2 | Crate metadata for crates.io | ✅ | `cargo publish --dry-run` passes for `tokio-dotnet-core` and `tokio-dotnet-macros` |
| 1.3 | NuGet packages for runtime and generator | ✅ packable, not published | `dotnet pack` into `artifacts/nuget/` |
| 1.4 | **Decide the name** | 🟡 owner decision | Repo is `rustify`, code is `tokio-dotnet`. Registry names are effectively permanent. See section 5 |
| 1.5 | Publish to crates.io and NuGet | 🟡 needs 1.4 + API tokens | Irreversible (crates.io versions cannot be deleted); not done without explicit go-ahead |

## 2. Make the repository findable

| # | Step | Status | Notes |
|---|---|---|---|
| 2.1 | Repo description and topics | ✅ | topics: rust, dotnet, csharp, ffi, interop, tokio, async, nativeaot, source-generator, bindings |
| 2.2 | README leads with search phrases | ✅ | "call async Rust from C#", Rust/.NET interop, `Task<T>`, `IAsyncEnumerable<T>` |
| 2.3 | "Not affiliated with Tokio/Microsoft" note | ✅ | README, License section |
| 2.4 | Runnable sample (`cargo build && dotnet run`) | ✅ | `samples/QuickStart` |
| 2.5 | Social preview image | ⬜ | Set in GitHub repo settings (manual upload) |

## 3. Position against existing tools

| # | Step | Status | Notes |
|---|---|---|---|
| 3.1 | Research current state of csbindgen, UniFFI C#, Interoptopus, others | ✅ | Every claim sourced |
| 3.2 | Fair comparison table in the README | ✅ | Only verified claims |

## 4. Launch channels (do after 1.5)

Nothing in this section is posted automatically: these go out under your name, to communities
with their own rules, and should be timed for when someone can actually install the project.

| # | Channel | Status | Asset |
|---|---|---|---|
| 4.1 | Technical blog post | 🟡 draft, needs your rewrite (7.3) | [blog/async-rust-to-dotnet.md](blog/async-rust-to-dotnet.md) |
| 4.2 | r/rust, r/dotnet, r/csharp | 🟡 drafts below | Post the article, not the bare repo |
| 4.3 | This Week in Rust | ⬜ | Via r/rust; see 7.2 |
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

## 5. Name decision (needed before anything is published)

Checked against the live registries on 2026-11-26:

| Name | crates.io | NuGet | Notes |
|---|---|---|---|
| `tokio-dotnet`, `-core`, `-macros` / `TokioDotnet.*` | free | free | No Tokio naming policy was found, which is not the same as permission. Reads as an official Tokio project; consider asking the Tokio maintainers first |
| `rustify` / `Rustify` | **taken** (HTTP client, ~15M downloads) | **taken** | The GitHub repo name collides with unrelated packages on both registries |

Options: (a) keep `tokio-dotnet` and rename the GitHub repo to match; (b) pick a new neutral name
for crates, packages and repo. Either way the repo should not stay `rustify`, because searching
that name leads to other projects. The Rust Foundation trademark policy allows "Rust" in a name
when it indicates compatibility but not when it could look official.

## 6. Engineering deliverables for this plan

| Deliverable | Where | Verified by |
|---|---|---|
| CI on Ubuntu, macOS, Windows | `.github/workflows/ci.yml` | Green on all three (tests, sample, NativeAOT) |
| Crate metadata | `Cargo.toml`, `crates/*/Cargo.toml` | `cargo publish --dry-run` for both library crates |
| NuGet packaging | `dotnet/TokioDotnet.Runtime`, `dotnet/TokioDotnet.Generator` (+ `build/*.targets`) | `dotnet pack`; a consumer using only the two `.nupkg` files built, ran and AOT-published on macOS |
| One-property consumer setup | `TokioDotnetCargoTargetDir` | Used by the tests, the AOT smoke app and the sample |
| Quick-start sample | `samples/QuickStart` | Run by `verify.sh` locally and in CI |
| Comparison table | `README.md` | Sourced from the research below |
| Blog post draft | `docs/blog/async-rust-to-dotnet.md` | Fact-checked against the code by its author; see caveat in 7.3 |

To build the packages locally: `dotnet pack dotnet/TokioDotnet.Runtime -c Release -o artifacts/nuget`
and the same for `dotnet/TokioDotnet.Generator`.

## 7. Research (2026-11-26)

Gathered from each project's README, docs and issue tracker. Items marked unverified could not be
read directly.

### 7.1 Positioning: this project is not the first

Interoptopus, BoltFFI and WeaveFFI already turn async Rust into a .NET `Task` with a
`CancellationToken`. BoltFFI and the very new UniFFISharp also expose streams as
`IAsyncEnumerable<T>`, and UniFFISharp uses a Roslyn source generator. **Do not claim "first" or
"only" anywhere.** What was found nowhere else:

- pull-based streaming with backpressure (BoltFFI documents that a full buffer drops events);
- a Rust-owned `Vec<T>` returned without a copy as `IMemoryOwner<T>` from an async call;
- `ReadOnlyMemory<T>` inputs kept pinned for the whole async call.

Interoptopus (about 500 stars, active) is the closest established alternative; BoltFFI (about 900
stars, very active) is the closest on features. csbindgen is the only C# entry in awesome-rust and
its maintainer has said async is out of scope
([issue 36](https://github.com/Cysharp/csbindgen/issues/36)).

### 7.2 Channel rules

| Channel | What the rules say |
|---|---|
| This Week in Rust | Project updates are no longer accepted by PR; editors pick them from r/rust. Articles go by PR to `draft/`. **"We don't accept articles written by LLMs."** Crate of the Week is nominated on the users forum |
| awesome-rust | Needs more than 50 stars or 2,000 crates.io downloads. Section: Development tools → FFI → C# |
| awesome-dotnet (quozd) | "Actively maintained, stable, documented, tests"; section Interoperability. The list looks stale (about 100 open PRs) |
| Show HN | Must be something people can try; excludes quickly generated one-off projects; do not ask for upvotes |
| r/rust, r/dotnet, r/csharp | **Unverified** (Reddit blocked reading). Secondhand: r/rust removes "code dumps"; use the project flair and lead with substance |
| C# Digest | No submission route found, paid placement only |
| JetBrains dotInsights | Suggestions by email to the .NET advocates team |

### 7.3 Caveat: this project was built with an AI agent

That is relevant to three of the channels above (TWiR's LLM rule, r/rust's code-dump rule, Show
HN's "quickly generated" exclusion). The blog draft in `docs/blog/` was written by an AI agent and
should be treated as raw material: rewrite it in your own words and from your own understanding
before publishing, and be upfront about how the project was built. Communities respond far better
to disclosed AI assistance plus real ownership of the code than to discovering it later.

### 7.4 Where the demand is

People who have hit this exact problem. Useful for understanding needs; reply only where the
project genuinely solves the question, and say you are the author.

| Where | Date | What they needed | State |
|---|---|---|---|
| [csbindgen #36](https://github.com/Cysharp/csbindgen/issues/36) | 2023-06 | Crossing the async boundary from Rust to C# | Closed: "can not" |
| [users.rust-lang.org thread](https://users.rust-lang.org/t/async-ffi-crate-for-c-async-calls/87946) | 2023-01 | Async calls from C# via async-ffi | Closed, no solution |
| [ScyllaDB C# driver #146](https://github.com/scylladb/csharp-rs-driver/issues/146) | 2026-05 | Hand-rolled async bridge leaks GCHandles | Open |
| [DataFusionSharp blog post](https://www.npiontko.pro/2026/04/26/rust-csharp-async-interop) | 2026-04 | Hand-built Tokio-to-Task bridge with cancellation | Author found no library |
| [Interoptopus #282](https://github.com/ralfbiedert/interoptopus/issues/282) | 2026-05 | Cancellation via tokio-util tokens instead of abort | Open |
| [Interoptopus #165](https://github.com/ralfbiedert/interoptopus/issues/165) | 2025-04 | Fewer allocations per async call | Open |
| [uniffi-bindgen-cs #165](https://github.com/NordSecurity/uniffi-bindgen-cs/issues/165) | 2026-02 | Async callbacks generate sync types | Open, unanswered |
| [wit-bindgen #1759](https://github.com/bytecodealliance/wit-bindgen/issues/1759) | 2026-10 | C# async export traps on cancel | Open |

The strongest signal is teams hand-building this bridge inside real products (ScyllaDB's C#
driver, DataFusionSharp, LanceDB). They are the natural first users and the best source of feedback.

## 8. Suggested order from here

1. Decide the name (section 5) and rename the repo to match.
2. Publish 0.1.0 to crates.io and NuGet (needs your API tokens and an explicit go-ahead).
3. Rewrite the blog draft in your own voice and publish it.
4. Post to r/rust (project flair) and r/dotnet; TWiR picks project news up from r/rust.
5. Once past 50 stars or 2,000 downloads: awesome-rust PR; email dotInsights.
6. Build the flagship package (tokenizers for .NET) on the published crates.
