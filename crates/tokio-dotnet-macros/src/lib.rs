//! `#[dotnet_export]`: generates `extern "C"` shims for a struct / impl block
//! and records their schema for the C# source generator.

mod codegen_meta;
mod codegen_rust;
mod parser;

use proc_macro::TokenStream;
use quote::quote;
use syn::{parse_macro_input, Item};

#[proc_macro_attribute]
pub fn dotnet_export(_attr: TokenStream, item: TokenStream) -> TokenStream {
    let item = parse_macro_input!(item as Item);
    let extra = match &item {
        Item::Struct(s) => parser::parse_struct(s).map(|export| {
            codegen_meta::emit_struct(&export);
            codegen_rust::struct_shims(&export.name)
        }),
        Item::Impl(i) => parser::parse_impl(i).map(|export| {
            codegen_meta::emit_impl(&export);
            codegen_rust::impl_shims(&export)
        }),
        other => Err(syn::Error::new_spanned(
            other,
            "#[dotnet_export] supports structs and inherent impl blocks",
        )),
    }
    .unwrap_or_else(|e| e.to_compile_error());
    quote!(#item #extra).into()
}
