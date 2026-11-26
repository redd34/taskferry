//! FFI ABI structs, the global Tokio runtime, and the task/stream bridges
//! used by `#[dotnet_export]`-generated shims.

pub mod memory;
pub mod panic;
pub mod runtime;
pub mod stream_bridge;
pub mod task_bridge;

pub use memory::{
    FfiBytes, FfiError, FfiOutcome, FfiOwnedSlice, FfiReadOnlySlice, FfiSlice, FfiString, IntoFfi,
    RustError, RustResult,
};
pub use task_bridge::{spawn_task, CompletionCallback, CompletionGuard};
