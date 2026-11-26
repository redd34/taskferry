using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace TokioDotnet.Generator;

/// <summary>
/// Reads <c>tokio-dotnet-bindings.json</c> (emitted by <c>#[dotnet_export]</c>, passed as
/// AdditionalFiles) and generates one partial SafeHandle class per exported Rust struct.
///
/// Native calls use unmanaged function pointers resolved once per library instead of
/// [LibraryImport]: a source generator cannot feed another generator, so emitted
/// [LibraryImport] partials would never be implemented. All signatures are blittable,
/// so there are no marshalling stubs and no runtime reflection (NativeAOT-safe).
/// </summary>
[Generator]
public sealed class BindingGenerator : IIncrementalGenerator
{
    private const string Callback = "delegate* unmanaged[Cdecl]<nint, void*, void>";

    private static readonly DiagnosticDescriptor InvalidManifest = new(
        "TDN001", "Invalid tokio-dotnet manifest", "Could not generate bindings from '{0}': {1}",
        "TokioDotnet", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var manifests = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith("tokio-dotnet-bindings.json", StringComparison.Ordinal))
            .Select(static (file, ct) => (file.Path, Text: file.GetText(ct)?.ToString() ?? ""));

        context.RegisterSourceOutput(manifests, static (spc, manifest) =>
        {
            try
            {
                Generate(spc, manifest.Text);
            }
            catch (Exception e)
            {
                spc.ReportDiagnostic(Diagnostic.Create(InvalidManifest, Location.None, manifest.Path, e.Message));
            }
        });
    }

    private static void Generate(SourceProductionContext spc, string json)
    {
        var root = (Dictionary<string, object?>)Json.Parse(json)!;
        var entries = ((List<object?>)root["entries"]!).Cast<Dictionary<string, object?>>().ToList();

        foreach (var library in entries.GroupBy(e => (string)e["library"]!))
        {
            string ns = Pascal(library.Key);
            var structs = library.Where(e => (string)e["kind"]! == "struct").ToList();
            var methodsOf = library
                .Where(e => (string)e["kind"]! == "impl")
                .GroupBy(e => (string)e["struct"]!)
                .ToDictionary(g => g.Key, g => g.SelectMany(e => ((List<object?>)e["methods"]!).Cast<Dictionary<string, object?>>()).ToList());

            var native = new StringBuilder();
            foreach (var s in structs)
            {
                string name = (string)s["name"]!;
                var methods = methodsOf.TryGetValue(name, out var m) ? m : new List<Dictionary<string, object?>>();

                NativeField(native, (string)s["free"]!, new[] { "nint" });
                var body = new StringBuilder();
                var members = new HashSet<string> { name }; // CS0542: a member cannot be named like its type
                foreach (var method in methods)
                {
                    string member = MemberName((string)method["name"]!, (string)method["kind"]!);
                    if (!members.Add(member) || ((string)method["kind"]! == "stream" && !members.Add(member + "Open")))
                    {
                        throw new InvalidOperationException(
                            $"Rust method '{name}::{method["name"]}' maps to the C# member '{member}', which is already taken; rename it in Rust");
                    }
                    EmitMethod(body, native, name, method);
                }

                spc.AddSource($"{ns}.{name}.g.cs", $$"""
                    {{Header}}
                    namespace {{ns}};

                    {{Doc((string)s["doc"]!, "")}}public sealed partial class {{name}} : RustSafeHandle
                    {
                        private {{name}}(nint handle) : base(handle) { }

                        protected override unsafe bool ReleaseHandle()
                        {
                            // SAFETY: runs exactly once, after every AddRef'd call has finished;
                            // in-flight async calls hold their own Arc clone on the Rust side.
                            NativeMethods.{{s["free"]}}(handle);
                            return true;
                        }
                    {{body}}}

                    """);
            }

            spc.AddSource($"{ns}.NativeMethods.g.cs", $$"""
                {{Header}}
                namespace {{ns}};

                /// <summary>Function pointers into the <c>{{library.Key}}</c> native library.</summary>
                internal static unsafe class NativeMethods
                {
                    internal static readonly RustLibrary Library = RustLibrary.Load("{{library.Key}}", typeof(NativeMethods).Assembly);

                {{native}}}

                """);
        }
    }

    private const string Header = """
        // <auto-generated by TokioDotnet.Generator />
        #nullable enable
        using System;
        using System.Buffers;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using TokioDotnet.Runtime;

        """;

    private static void NativeField(StringBuilder native, string symbol, IEnumerable<string> parameters)
    {
        string type = $"delegate* unmanaged[Cdecl]<{string.Join(", ", parameters.Concat(new[] { "void" }))}>";
        native.AppendLine($"    internal static readonly {type} {symbol} = ({type})Library.GetExport(\"{symbol}\");");
    }

    private static void EmitMethod(StringBuilder sb, StringBuilder native, string cls, Dictionary<string, object?> m)
    {
        string name = (string)m["name"]!, symbol = (string)m["symbol"]!, kind = (string)m["kind"]!, ret = (string)m["ret"]!;
        bool hasSelf = (bool)m["has_self"]!;
        bool isAsync = kind == "async";
        // Async and stream methods outlive the call, so they cannot take spans.
        bool deferred = kind is "async" or "stream";

        var decls = new List<string>();     // C# parameter declarations
        var pres = new List<string>();      // statements before the call
        var fixeds = new List<string>();    // `fixed` statements wrapping the call
        var args = new List<string>();      // native call arguments
        var nativeParams = new List<string>();
        if (hasSelf)
        {
            args.Add("this.handle"); // `this.`: a parameter named `handle` must not shadow it
            nativeParams.Add("nint");
        }

        foreach (var p in ((List<object?>)m["params"]!).Cast<Dictionary<string, object?>>())
        {
            string camel = Camel((string)p["name"]!), n = "@" + camel, local = "__p_" + camel, type = (string)p["type"]!;
            if (type == "bool")
            {
                decls.Add($"bool {n}");
                args.Add($"(byte)({n} ? 1 : 0)");
                nativeParams.Add("byte");
            }
            else if (type is "str" or "string")
            {
                decls.Add($"string {n}");
                pres.Add($"byte[] {local} = Encoding.UTF8.GetBytes({n});");
                if (type == "str" && isAsync)
                {
                    // Borrowed by the Rust future: pinned until completion.
                    args.Add($"__tdn_ctx.Pin<byte>({local})");
                }
                else
                {
                    // Used (or copied by Rust) before the native call returns.
                    fixeds.Add($"fixed (byte* {local}_ptr = {local})");
                    args.Add($"new FfiReadOnlySlice({local}_ptr, {local}.Length)");
                }
                nativeParams.Add("FfiReadOnlySlice");
            }
            else if (Element(type) is { } element)
            {
                if (type.StartsWith("slice<", StringComparison.Ordinal) && isAsync)
                {
                    decls.Add($"ReadOnlyMemory<{element}> {n}");
                    args.Add($"__tdn_ctx.Pin({n})");
                }
                else
                {
                    decls.Add($"{(deferred ? "ReadOnlyMemory" : "ReadOnlySpan")}<{element}> {n}");
                    fixeds.Add($"fixed ({element}* {local}_ptr = {n}{(deferred ? ".Span" : "")})");
                    args.Add($"new FfiReadOnlySlice({local}_ptr, {n}.Length)");
                }
                nativeParams.Add("FfiReadOnlySlice");
            }
            else
            {
                decls.Add($"{Prim(type)} {n}");
                args.Add(n);
                nativeParams.Add(Prim(type));
            }
        }

        // (C-ABI type, managed type, converter) of the return value.
        (string Native, string Managed, string Conv) r = ret switch
        {
            "unit" => ("byte", "byte", "IdentityConverter<byte>"),
            "bool" => ("byte", "bool", "BoolConverter"),
            "string" => ("FfiOwnedSlice", "string", "StringConverter"),
            "self" => ("nint", cls, ""),
            _ when Element(ret) is { } e => ("FfiOwnedSlice", $"IMemoryOwner<{e}>", $"OwnedSliceConverter<{e}>"),
            _ => (Prim(ret), Prim(ret), $"IdentityConverter<{Prim(ret)}>"),
        };

        string member = MemberName(name, kind);
        // A Rust parameter may itself be called `cancellation_token`.
        string ct = decls.Any(d => d.EndsWith(" @cancellationToken", StringComparison.Ordinal))
            ? "__tdn_cancellationToken"
            : "cancellationToken";
        string mod = hasSelf ? "" : "static ";
        string doc = Doc((string)m["doc"]!, "    ");
        string pre = string.Concat(pres.Select(s => $"        {s}\n"));
        string fix = string.Concat(fixeds.Select(s => $"            {s}\n"));
        // SafeHandle ref-count: the Rust object cannot be freed during the native call.
        string addRef = hasSelf ? "bool __tdn_ref = false;\n        " : "";
        string enter = hasSelf ? "\n            DangerousAddRef(ref __tdn_ref);" : "";
        string release = hasSelf ? "if (__tdn_ref) DangerousRelease();" : "";

        if (isAsync)
        {
            NativeField(native, symbol, nativeParams.Concat(new[] { "nint", "nint", Callback }));
            string task = ret == "unit" ? "Task" : $"Task<{r.Managed}>";
            string callArgs = string.Join(", ", args.Concat(new[] { "__tdn_ctx.CancelToken", "__tdn_ctx.AllocState()", "RustTaskBridge.Callback" }));
            sb.Append($$"""

                {{doc}}    public {{mod}}unsafe {{task}} {{member}}({{string.Join(", ", decls.Concat(new[] { $"CancellationToken {ct} = default" }))}})
                    {
                {{pre}}        var __tdn_ctx = new RustTaskContext<{{r.Native}}, {{r.Managed}}, {{r.Conv}}>(NativeMethods.Library, {{ct}});
                        {{addRef}}try
                        {{{enter}}
                {{fix}}            NativeMethods.{{symbol}}({{callArgs}});
                        }
                        catch
                        {
                            // The native call never started (e.g. disposed handle): no callback will come.
                            __tdn_ctx.Abort();
                            throw;
                        }
                        finally
                        {
                            {{release}}
                        }
                        return __tdn_ctx.Task;
                    }

                """);
            return;
        }

        string outNative = kind == "stream" ? "nint" : r.Native;
        NativeField(native, symbol, nativeParams.Concat(new[] { $"FfiOutcome<{outNative}>*" }));
        string syncCall = $$"""
            {{pre}}        FfiOutcome<{{outNative}}> __tdn_out = default;
                    {{addRef}}try
                    {{{enter}}
            {{fix}}            NativeMethods.{{symbol}}({{string.Join(", ", args.Concat(new[] { "&__tdn_out" }))}});
                    }
                    finally
                    {
                        {{release}}
                    }
            """;

        if (kind == "stream")
        {
            string argNames = string.Join(", ", decls.Select(d => d.Substring(d.LastIndexOf(' ') + 1)));
            sb.Append($$"""

                {{doc}}    public {{mod}}async IAsyncEnumerable<{{r.Managed}}> {{member}}({{string.Join(", ", decls.Concat(new[] { $"[EnumeratorCancellation] CancellationToken {ct} = default" }))}})
                    {
                        await using var __tdn_e = new RustAsyncEnumerator<{{r.Native}}, {{r.Managed}}, {{r.Conv}}>(NativeMethods.Library, {{member}}Open({{argNames}}), {{ct}});
                        while (await __tdn_e.MoveNextAsync().ConfigureAwait(false)) yield return __tdn_e.Current;
                    }

                    private {{mod}}unsafe nint {{member}}Open({{string.Join(", ", decls)}})
                    {
                {{syncCall}}
                        return __tdn_out.Unwrap();
                    }

                """);
            return;
        }

        (string returnType, string result) = kind == "ctor"
            ? (cls, $"return new {cls}(__tdn_out.Unwrap());")
            : ret == "unit"
                ? ("void", "__tdn_out.Unwrap();")
                : (r.Managed, $"return {r.Conv}.Convert(__tdn_out.Unwrap());");
        sb.Append($$"""

            {{doc}}    public {{mod}}unsafe {{returnType}} {{member}}({{string.Join(", ", decls)}})
                {
            {{syncCall}}
                    {{result}}
                }

            """);
    }

    /// <summary>C# member name: <c>new</c> -> <c>Create</c>, async methods get an <c>Async</c> suffix.</summary>
    private static string MemberName(string snake, string kind)
    {
        if (kind == "ctor" && snake == "new") return "Create";
        string name = Pascal(snake);
        return kind != "async" || name.EndsWith("Async", StringComparison.Ordinal) ? name : name + "Async";
    }

    /// <summary>C# element type of <c>slice&lt;T&gt;</c> / <c>vec&lt;T&gt;</c>, else null.</summary>
    private static string? Element(string type)
    {
        int open = type.IndexOf('<');
        return open < 0 ? null : Prim(type.Substring(open + 1, type.Length - open - 2));
    }

    private static string Prim(string rust) => rust switch
    {
        "u8" => "byte",
        "i8" => "sbyte",
        "u16" => "ushort",
        "i16" => "short",
        "u32" => "uint",
        "i32" => "int",
        "u64" => "ulong",
        "i64" => "long",
        "usize" => "nuint",
        "isize" => "nint",
        "f32" => "float",
        "f64" => "double",
        _ => throw new NotSupportedException($"unsupported type '{rust}'"),
    };

    private static string Pascal(string snake) =>
        string.Concat(snake.Split('_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));

    private static string Camel(string snake)
    {
        string pascal = Pascal(snake);
        return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
    }

    private static string Doc(string doc, string indent)
    {
        if (doc.Length == 0) return "";
        string escaped = doc.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var lines = new[] { "<summary>" }.Concat(escaped.Split('\n')).Concat(new[] { "</summary>" });
        return string.Concat(lines.Select(l => $"{indent}/// {l}\n"));
    }

    /// <summary>Minimal JSON reader (avoids shipping a JSON dependency with the analyzer).</summary>
    private sealed class Json
    {
        private readonly string _s;
        private int _i;

        private Json(string s) => _s = s;

        public static object? Parse(string s) => new Json(s).Value();

        private void SkipWhitespace()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private object? Value()
        {
            SkipWhitespace();
            switch (_s[_i])
            {
                case '{':
                {
                    _i++;
                    var map = new Dictionary<string, object?>();
                    SkipWhitespace();
                    if (_s[_i] == '}') { _i++; return map; }
                    while (true)
                    {
                        SkipWhitespace();
                        string key = String();
                        SkipWhitespace();
                        Expect(':');
                        map[key] = Value();
                        SkipWhitespace();
                        if (_s[_i] == '}') { _i++; return map; }
                        Expect(',');
                    }
                }
                case '[':
                {
                    _i++;
                    var list = new List<object?>();
                    SkipWhitespace();
                    if (_s[_i] == ']') { _i++; return list; }
                    while (true)
                    {
                        list.Add(Value());
                        SkipWhitespace();
                        if (_s[_i] == ']') { _i++; return list; }
                        Expect(',');
                    }
                }
                case '"': return String();
                case 't': _i += 4; return true;
                case 'f': _i += 5; return false;
                case 'n': _i += 4; return null;
                default:
                {
                    int start = _i;
                    while (_i < _s.Length && "+-.eE0123456789".IndexOf(_s[_i]) >= 0) _i++;
                    if (_i == start) throw new FormatException($"unexpected '{_s[_i]}' at offset {_i}");
                    return double.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture);
                }
            }
        }

        private void Expect(char c)
        {
            if (_s[_i] != c) throw new FormatException($"expected '{c}' at offset {_i}");
            _i++;
        }

        private string String()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (_s[_i] != '"')
            {
                char c = _s[_i++];
                if (c != '\\') { sb.Append(c); continue; }
                c = _s[_i++];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16)); _i += 4; break;
                    default: sb.Append(c); break;
                }
            }
            _i++;
            return sb.ToString();
        }
    }
}
