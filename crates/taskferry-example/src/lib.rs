//! Reference cdylib for `taskferry`.

mod hf_tokenizer;
mod stress_ops;

use std::ffi::c_void;

use taskferry_core::runtime::CancellationToken;
use taskferry_core::{spawn_task, CompletionCallback, CompletionGuard, FfiOwnedSlice, FfiReadOnlySlice};

/// Phase-1 hand-wired export (no macro): what `#[dotnet_export]` generates for
/// `async fn(data: &[u8]) -> Vec<u8>`. Kept as a test of the core ABI on its own.
///
/// # Safety
/// `data` must stay pinned until `callback` has run; `cancel` is null or a live token.
#[no_mangle]
pub unsafe extern "C" fn taskferry_example_transform_async(
    data: FfiReadOnlySlice<u8>,
    cancel: *const CancellationToken,
    state: *mut c_void,
    callback: CompletionCallback,
) {
    // SAFETY: .NET passes a fresh GCHandle and its static completion callback.
    let guard = unsafe { CompletionGuard::<FfiOwnedSlice<u8>>::new(state, callback) };
    // SAFETY: .NET keeps `data` pinned until the callback, which only fires
    // after the future below (the sole user of `data`) has been dropped.
    let data: &'static [u8] = unsafe { data.as_slice() };
    // SAFETY: `cancel` validity is the caller's contract.
    unsafe {
        spawn_task::<Vec<u8>, _>(guard, cancel, async move {
            tokio::task::yield_now().await;
            Ok(data.iter().map(|b| b ^ 0xFF).collect())
        })
    };
}
