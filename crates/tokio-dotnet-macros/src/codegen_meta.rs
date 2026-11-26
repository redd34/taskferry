//! Emits the FFI schema consumed by `TokioDotnet.Generator`.
//!
//! Each macro invocation rewrites `<target>/tokio-dotnet-bindings/<crate>.json`
//! with the crate's exports seen so far in this compilation (sorted, so the
//! result is deterministic), then rebuilds `<target>/tokio-dotnet-bindings.json`
//! from all per-crate files.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use crate::codegen_rust::{free_symbol, method_symbol};
use crate::parser::{ImplExport, Kind, StructExport};

pub fn emit_struct(export: &StructExport) {
    let name = &export.name;
    let json = format!(
        r#"{{"kind":"struct","library":"{}","name":"{}","free":"{}","doc":"{}"}}"#,
        library(),
        name,
        free_symbol(name),
        escape(&export.doc)
    );
    write_fragment(&format!("{name}.struct"), &json);
}

pub fn emit_impl(export: &ImplExport) {
    let Some(first) = export.methods.first() else { return };
    let name = &export.name;
    let methods: Vec<String> = export
        .methods
        .iter()
        .map(|m| {
            let kind = match m.kind {
                Kind::Ctor => "ctor",
                Kind::Sync => "sync",
                Kind::Async => "async",
                Kind::Stream => "stream",
            };
            let params: Vec<String> = m
                .params
                .iter()
                .map(|p| format!(r#"{{"name":"{}","type":"{}"}}"#, p.name, p.ty.schema()))
                .collect();
            format!(
                r#"{{"name":"{}","symbol":"{}","kind":"{}","has_self":{},"params":[{}],"ret":"{}","doc":"{}"}}"#,
                m.name,
                method_symbol(name, &m.name),
                kind,
                m.has_self,
                params.join(","),
                m.ret.schema(),
                escape(&m.doc)
            )
        })
        .collect();
    let json = format!(
        "{{\"kind\":\"impl\",\"library\":\"{}\",\"struct\":\"{}\",\"methods\":[\n  {}\n]}}",
        library(),
        name,
        methods.join(",\n  ")
    );
    // Keyed by the first method so several impl blocks of one struct don't collide.
    write_fragment(&format!("{name}.impl.{}", first.name), &json);
}

/// cdylib name of the crate being compiled (what .NET loads).
fn library() -> String {
    std::env::var("CARGO_CRATE_NAME").unwrap_or_default()
}

fn escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

/// `$CARGO_TARGET_DIR`, else `<workspace root>/target` (root = nearest ancestor with Cargo.lock).
fn target_dir() -> Option<PathBuf> {
    if let Some(dir) = std::env::var_os("CARGO_TARGET_DIR") {
        return Some(dir.into());
    }
    let manifest_dir = PathBuf::from(std::env::var_os("CARGO_MANIFEST_DIR")?);
    let root = manifest_dir.ancestors().find(|d| d.join("Cargo.lock").exists())?;
    Some(root.join("target"))
}

/// Fragments emitted by this compiler process, keyed by stem. One rustc process
/// expands every `#[dotnet_export]` of a crate, so after the last expansion this
/// holds exactly the crate's current exports - nothing stale survives a rebuild.
static FRAGMENTS: Mutex<BTreeMap<String, String>> = Mutex::new(BTreeMap::new());

// Best effort: a failed write must not break compilation (e.g. read-only docs builds).
fn write_fragment(stem: &str, json: &str) {
    let Some(target) = target_dir() else { return };
    let dir = target.join("tokio-dotnet-bindings");
    let _ = fs::create_dir_all(&dir);

    let entries = {
        let mut fragments = FRAGMENTS.lock().unwrap_or_else(|e| e.into_inner());
        fragments.insert(format!("{}.{stem}", library()), json.to_string());
        fragments.values().cloned().collect::<Vec<_>>().join(",\n")
    };
    write_if_changed(&dir.join(format!("{}.json", library())), &entries);

    // Merge every library's entries (sorted by file name) into the manifest.
    let Ok(files) = fs::read_dir(&dir) else { return };
    let mut paths: Vec<PathBuf> = files
        .filter_map(|e| Some(e.ok()?.path()))
        .filter(|p| p.extension().is_some_and(|x| x == "json"))
        .collect();
    paths.sort();
    let libraries: Vec<String> = paths.iter().filter_map(|p| fs::read_to_string(p).ok()).collect();
    let manifest = format!("{{\"version\":1,\"entries\":[\n{}\n]}}\n", libraries.join(",\n"));
    write_if_changed(&target.join("tokio-dotnet-bindings.json"), &manifest);
}

/// Atomic (temp file + rename), and a no-op when unchanged so .NET builds stay incremental.
fn write_if_changed(path: &Path, content: &str) {
    if fs::read_to_string(path).ok().as_deref() == Some(content) {
        return;
    }
    let tmp = path.with_extension(format!("tmp{}", std::process::id()));
    if fs::write(&tmp, content).is_ok() {
        let _ = fs::rename(&tmp, path);
    }
}
