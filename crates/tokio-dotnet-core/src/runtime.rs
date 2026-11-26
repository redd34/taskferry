//! Global multi-thread Tokio runtime and the cancellation-token handles that
//! .NET `CancellationToken`s are bridged to.

use std::sync::OnceLock;

use tokio::runtime::{Builder, Runtime};
pub use tokio_util::sync::CancellationToken;

pub fn runtime() -> &'static Runtime {
    static RUNTIME: OnceLock<Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| {
        Builder::new_multi_thread()
            .enable_all()
            .thread_name("tokio-dotnet")
            .build()
            .expect("failed to start the tokio-dotnet runtime")
    })
}

/// Creates a token owned by .NET (`RustCancellationHandle`).
#[no_mangle]
pub extern "C" fn tokio_dotnet_cancel_token_new() -> *const CancellationToken {
    Box::into_raw(Box::new(CancellationToken::new()))
}

/// # Safety
/// `token` must be a live pointer from `tokio_dotnet_cancel_token_new`.
#[no_mangle]
pub unsafe extern "C" fn tokio_dotnet_cancel_token_cancel(token: *const CancellationToken) {
    // SAFETY: the .NET SafeHandle ref-count keeps the box alive during this call.
    unsafe { &*token }.cancel();
}

/// # Safety
/// `token` must come from `tokio_dotnet_cancel_token_new` and not be used again.
#[no_mangle]
pub unsafe extern "C" fn tokio_dotnet_cancel_token_free(token: *const CancellationToken) {
    // SAFETY: SafeHandle.ReleaseHandle runs exactly once, after all other users.
    // Spawned tasks hold their own clones, so they are unaffected.
    drop(unsafe { Box::from_raw(token as *mut CancellationToken) });
}
