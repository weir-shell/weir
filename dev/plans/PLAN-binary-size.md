# weir — shrinking the AOT binary (measurement study)

Status: LANDED on plan/hunt3 (2026-10-16) — the no-cost set
([D:binary-size], 20.7 → 17.8 MB). Ruled (user): keep the two framework
stubs; keep OptimizationPreference=Speed; the builtinDocs-as-data refactor
goes to BACKLOG; UseSystemResourceKeys rejected (user-visible text).

Baseline: 0bf815c, linux-x64 NativeAOT, .NET 10.0.13 ILC, FSharp.Core
10.1.302 — **20,707,400 bytes**. Every number below is a real publish
(`dotnet publish src/Weir -c Release -r linux-x64`, one property or
substitution changed vs baseline unless marked cumulative). The ILC is
deterministic here: rebuilding the baseline reproduced the byte count
exactly; switches with no effect land at ±16 bytes.

## Where the bytes are

File layout (`size -A`): `__managedcode` 9.2 MB, `.rodata` 6.9 MB
(dehydrated EETypes, frozen objects, metadata, resources), unwind info
3.5 MB (`.eh_frame` 1.6 + `.dotnet_eh_table` 1.5 + `.eh_frame_hdr` 0.4),
native runtime `.text` 0.68 MB. No brotli/zlib-ng symbols are linked:
weir never sets `AutomaticDecompression`, so System.IO.Compression is
trimmed away entirely. System.Net.Quic is absent too (Http3Support is
already false under AOT, Kestrel never calls UseQuic).

Attribution from the ILC mstat (methods = code + GC + EH info, plus
EETypes and frozen data; blobs such as reflection metadata 1.16 MB and
the stack-trace map 0.32 MB are not per-assembly):

| assembly | bytes |
|---|---|
| System.Private.CoreLib | 5,817,389 |
| Weir | 3,645,255 |
| FSharp.Core | 1,748,697 |
| System.Private.Xml | 683,376 |
| Microsoft.AspNetCore.Server.Kestrel.Core | 680,475 |
| System.Net.Http | 512,142 |
| System.Security.Cryptography | 498,636 |
| System.Text.RegularExpressions | 436,697 |
| System.Collections.Concurrent | 267,953 |
| System.Net.Security | 179,941 |
| System.Private.TypeLoader | 145,745 |
| FParsec + FParsecCS | 209,910 |

Top namespaces: Weir 3.42 MB; CoreLib System 2.19 MB and
System.Collections.Generic 1.73 MB (generic instantiations over weir's
types); Microsoft.FSharp.Core 1.16 MB; System.Runtime.CompilerServices
0.47 MB (async state machines); Microsoft.FSharp.Collections 0.43 MB;
System.Xml 0.42 MB; System.Net.Http 0.35 MB; Concurrent 0.27 MB;
Kestrel Internal.Http 0.22 MB; Weir startup code 0.22 MB; Regex
Symbolic (NonBacktracking) 0.21 MB; Buffers 0.20 MB; Xml.Schema
0.19 MB; Regex 0.17 MB; Threading 0.17 MB; Crypto 0.15 MB + X509
0.14 MB; FParsec 0.13 MB; System.IO 0.12 MB; Kestrel Http2 0.10 MB and
Http3 0.10 MB.

Single items worth naming:

- **`Builtins.builtinDocs`** is one 287 KB method, plus the Builtins
  static initializer at 118 KB and Parser's at 57 KB: the ~400-entry
  docs Map literal compiles to straight-line construction code.
- **F# generic equality**: FSharp.Core's `HashCompare` comparer classes
  (`arrayEqualityComparer`, `structuralEqualityComparer`,
  `getEqualityComparer` closures, `EqualityComparerER/PER`) are
  instantiated for ~650 types, about 410 KB of EETypes, which also pulls in
  CoreLib's `EqualityComparer<T>`/`ObjectEqualityComparer<T>` per type
  (another 286 KB). The instantiations come from the virtual
  Equals of F# lists, options, records and unions. FSharp.Core 8.0.403 has the same
  shape (+528 bytes), so a version pin does not help.
- **printf / %A**: PrintfImpl 113 KB, StructuredPrintfImpl 56 KB,
  PrintfFormat 46 KB, FSharp.Reflection 17 KB, about 230 KB plus the
  reflection metadata it needs. The compiler-generated `ToString()`
  (`%+A`) on every record and union keeps it alive. Weir's own 9
  printf-family call sites (2 sprintf, 4 printfn, 3 eprintf/eprintfn,
  0 failwithf) do not. Rewriting the call sites saves nothing.
- Weir's compiler-generated `CompareTo` is 97 KB across 118 types (TypedKind
  and ExprKind are 27 KB of it); `Equals` 118 KB, `GetHashCode` 52 KB.

## Is trimming working?

Yes. ILC trims every assembly fully: the rsp has no `--root`,
`--conditionalroot` or `--defaultrooting`. The repo has no
TrimmerRootAssembly, IsTrimmable, rd.xml, DynamicDependency or
DynamicallyAccessedMembers. IL2104/IL3053 summarize 50 unanalyzable
reflection patterns: FSharp.Core's sformat.fs (%A, 21), reflect.fs (13),
printf.fs (13), seq.fs/array2.fs (4), and FParsecCS's
`FastGenericEqualityERComparer.Create<T>`. Such warnings mark code that
might break, and they keep nothing extra alive. Already on by
the AOT defaults: EventSourceSupport=false, MetadataUpdater=false,
Http3Support=false, UseSystemResourceKeys=false, generic method-body
folding, `Linq.Enumerable.IsSizeOptimized`.

## Levers, measured one at a time vs baseline

| lever | bytes | Δ bytes | Δ % | behaviour risk | effort |
|---|---|---|---|---|---|
| OptimizationPreference=Size (vs Speed) | 19,094,136 | −1,613,264 | −7.79 | +6–10% CPU on eval/check (below) | 1 line |
| OptimizationPreference unset (vs Speed) | 19,564,680 | −1,142,720 | −5.52 | between Size and Speed, and also loses static PGO | 1 line |
| StackTraceSupport=false (implies folding all) | 19,445,640 | −1,261,760 | −6.09 | no frame names in a runtime crash; weir output never prints a frame list | 1 line |
| IlcFoldIdenticalMethodBodies=true | 20,166,728 | −540,672 | −2.61 | folded frames in traces; delegate identity (weir has no such dependency) | 1 line |
| regex NonBacktracking factory stubbed (substitution) | 20,123,528 | −583,872 | −2.82 | none today: weir never passes the option; a future NonBacktracking user fails at match time | xml entry |
| Kestrel SelectProtocol pinned to Http1 (substitution) | 20,247,672 | −459,728 | −2.22 | none: `serve` listens Http1 only; HTTP/2 serving would need it reverted | xml entry |
| F# signature/optimization resources + Xml preloaded DTDs removed (substitution) | 20,363,336 | −344,064 | −1.66 | none: compiler-only data / XmlPreloadedResolver data | xml entries |
| SatelliteResourceLanguages=en | 20,437,064 | −270,336 | −1.31 | none: InvariantGlobalization never loads FSharp.Core's 13 satellites | 1 line |
| HttpActivityPropagationSupport=false | 20,661,464 | −45,936 | −0.22 | none: no Activity/listener, so no traceparent was ever injected | 1 line |
| XmlResolverIsNetworkingEnabledByDefault=false | 20,690,696 | −16,704 | −0.08 | none: weir's reader sets XmlResolver = null | 1 line |
| UseSystemResourceKeys=true | 20,703,288 | −4,112 | −0.02 | **user-visible**: every `{ex.Message}` weir echoes becomes a resource key (IO errors, JSON errors, regex errors, "serve: cannot listen … — …") | 1 line, REJECTED |
| DebuggerSupport=false | 20,707,416 | +16 | 0 | none | no effect |
| NullabilityInfoContextSupport=false | 20,707,416 | +16 | 0 | none | no effect |
| _AggressiveAttributeTrimming=true | 20,707,416 | +16 | 0 | not measured further | no effect |
| FSharp.Core 8.0.403 (vs 10.1.302) | 20,707,928 | +528 | 0 | n/a | no effect |
| EventSource / MetadataUpdater / Http3 switches | n/a | 0 | 0 | already off under AOT | n/a |
| Compression / QUIC | n/a | 0 | 0 | already not linked | n/a |

Not measured as builds, estimated from the mstat:

| lever | est. Δ | effort |
|---|---|---|
| builtinDocs as data (one embedded text resource, parsed on first use) instead of a 400-entry Map literal | −250 to −350 KB | mechanical refactor of ~1,580 lines (Builtins.fs 5865–7442) and its 23 readers stay unchanged |
| `[<NoComparison>]` where comparison is unused | ≤ −100 KB | per-type annotations; the compiler finds the needed ones |
| drop %A machinery (explicit `ToString` overrides on every record/union) | ≤ −230 KB + metadata | hundreds of types; poor return. A substitution stub of FSharp.Core's `anyToStringForPrintf` was tried, but ILC never matched the generic signature (IL2009) |
| avoid FSharp.Core generic equality on hot generic types | up to −0.7 MB | architectural (custom equality on Value/AST, fewer `'T list` element types); not recommended |

## Cumulative

| set | bytes | Δ % |
|---|---|---|
| Size + satellites + HttpActivity + XmlResolver | 18,761,208 | −9.40 |
| … + StackTraceSupport=false | 17,524,024 | −15.37 |
| … + resource substitutions | 17,175,864 | −17.05 |
| … + regex + Kestrel substitutions (everything, at Size) | **16,262,808** | **−21.46** |
| **recommended: everything except Size (Speed kept)** | **17,774,200** | **−14.17** |

## Throughput

Medians: startup = 40 runs of `weir -e 'print "x"'`; the others = 15 runs.
The binaries were interleaved and the runs held the e2e lock. seq1e6 =
`[1 .. 1000000] |> Seq.map … |> Seq.where … |> Seq.sum`; check =
`weir check` on 20k-line `lets` / `realistic` scripts from h3-D gen.py.

| case | baseline | Size only | recommended (Speed) | everything at Size |
|---|---|---|---|---|
| startup | 16.4 ms | 16.8 ms | 18.0 ms | 19.5 ms |
| seq1e6 | 524 ms | 554 ms (+5.7%) | 539 ms (+2.8%) | 559 ms (+6.6%) |
| check lets20k | 1092 ms | 1198 ms (+9.7%) | 1099 ms (+0.7%) | 1185 ms (+8.5%) |
| check realistic20k | 773 ms | 831 ms (+7.5%) | 793 ms (+2.6%) | 828 ms (+7.0%) |

Startup differences are within this machine's noise: repeated runs move
the median by ±3 ms, and an earlier run had Size at 17.4 vs 15.9 and
the full Size set at 15.4. Size consistently costs 6–10% on CPU-bound
work. The recommended set costs about 0–3%, which is within run-to-run
noise.

## Recommended set (committed)

Weir.fsproj: `StackTraceSupport=false`, `SatelliteResourceLanguages=en`,
`HttpActivityPropagationSupport=false`,
`XmlResolverIsNetworkingEnabledByDefault=false`, plus an
`IlcArg --substitution:` pointing at `src/Weir/ILLink.Substitutions.xml`.
That file removes the F# signature/optimization resources (Weir,
FParsec) and System.Private.Xml's preloaded DTDs, stubs
`SymbolicRegexRunnerFactory..ctor`, and pins Kestrel
`HttpConnection.SelectProtocol()` to Http1.

Gates on the clean publish of the commit (17,774,200 bytes, stamp ==
HEAD 5e6191b): unit suite 2234/2234 passed; AOT e2e
`e2e battery: all green` (883 ok, 0 failures), including the serve,
regex, XML and stamp-gate cells.

## Risks

- **Substitutions name framework internals.** A rename in a future
  runtime pack (the SDK-locked ASP.NET pack, or System.Text.RegularExpressions)
  fails the publish with IL2009, which is an error under warnaserror and was
  confirmed during this study. It never miscompiles silently. The fix is
  to update the signature or drop the entry.
- **Regex stub trap**: adding RegexOptions.NonBacktracking anywhere
  would produce a Regex whose factory is null. The xml comment says so.
- **Kestrel stub**: enabling HTTP/2 in `serve` requires deleting the
  entry. Today's ListenOptions are Http1-only.
- **No stack-trace data**: an unhandled crash, such as an exception on a
  thread weir does not guard or a FailFast, prints frames without method
  names. Weir's own top-level handler prints only `<Type>: <message>`,
  so no diagnostic text changes. Bug triage of a crash then needs a
  repro or the `.dbg` file.
- ILC does not track the substitution xml as an input, so a local
  incremental republish after editing only the xml reuses the stale
  object. Clean `obj/` or touch the fsproj.

## Needs a ruling

1. **OptimizationPreference=Size**: a further −1.51 MB (17.77 → 16.26 MB,
   −21.5% total) for +6–10% on check/eval CPU time.
2. **UseSystemResourceKeys**: rejected. It saves 4 KB and changes the text of
   every .NET exception message weir echoes.
3. **builtinDocs-as-data refactor**: about −0.3 MB for one mechanical session.
   It is worth doing only if size is a goal beyond the free wins.

## Method

mstat parsed by a throwaway F# script (System.Reflection.Metadata over
the mstat's `<Module>` IL streams: Methods/Types/Blobs/FrozenObjects/
RvaFields/ManifestResources), attributing by the owning type's assembly
and namespace. Variants used `-p:` properties and
`CustomAfterMicrosoftCommonTargets` for substitutions, so the tree
stayed clean.
