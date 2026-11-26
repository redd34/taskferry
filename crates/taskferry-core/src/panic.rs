//! Panic containment: no unwind ever crosses `extern "C"`.

use std::any::Any;
use std::future::Future;
use std::panic::{catch_unwind, AssertUnwindSafe};

use futures::FutureExt;

use crate::memory::{FfiOutcome, IntoFfi, RustError, RustResult};

pub fn panic_message(payload: Box<dyn Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        s.to_string()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "unknown panic payload".to_string()
    }
}

/// Runs a synchronous exported body, converting panics and errors to an outcome.
pub fn catch_sync<R: IntoFfi, F: FnOnce() -> RustResult<R>>(f: F) -> FfiOutcome<R::Ffi> {
    let result = catch_unwind(AssertUnwindSafe(f))
        .unwrap_or_else(|p| Err(RustError::panic(panic_message(p))));
    FfiOutcome::from_result(result)
}

/// Runs teardown code that may execute user `Drop` impls on a .NET thread:
/// inside the Tokio context (so drops may spawn) and with panics contained.
pub fn guarded_drop(f: impl FnOnce()) {
    let _context = crate::runtime::runtime().enter();
    if let Err(p) = catch_unwind(AssertUnwindSafe(f)) {
        eprintln!("taskferry: panic during drop: {}", panic_message(p));
    }
}

/// Awaits `fut`, converting a panic at any poll into `RustError::panic`.
pub async fn catch_async<R>(fut: impl Future<Output = RustResult<R>>) -> RustResult<R> {
    AssertUnwindSafe(fut)
        .catch_unwind()
        .await
        .unwrap_or_else(|p| Err(RustError::panic(panic_message(p))))
}
