module Corpus

// Bulk corpus comparison (the mined dotnet/fsharp snippets). Env-gated:
// set WEIR_CORPUS_DIR to a directory of *.snippet files; without it the
// test is a no-op skip. One-time triage tooling, not a CI gate — findings
// graduate into Pins.fs / divergences.md by hand.

open Expecto
open Oracle

[<Tests>]
let corpusTests =
    testList
        "Corpus"
        [ test "bulk accept/reject comparison" {
              match System.Environment.GetEnvironmentVariable "WEIR_CORPUS_DIR" with
              | null -> Tests.skiptest "WEIR_CORPUS_DIR not set"
              | dir ->
                  let results =
                      System.IO.Directory.GetFiles(dir, "*.snippet")
                      |> Array.map (fun f ->
                          let src = System.IO.File.ReadAllText f
                          f, src, weirVerdict src, fsharpVerdict src)

                  let bucket w f =
                      results |> Array.filter (fun (_, _, wv, fv) -> wv = w && fv = f)

                  let report = System.Text.StringBuilder()
                  let line (s: string) = report.AppendLine s |> ignore

                  line $"# Corpus comparison report ({results.Length} snippets)"
                  line ""
                  line $"- agree-accept: {(bucket Accept Accept).Length}"
                  line $"- agree-reject: {(bucket Reject Reject).Length}"
                  line $"- weir-accepts-fsharp-rejects (GOLD): {(bucket Accept Reject).Length}"
                  line $"- fsharp-accepts-weir-rejects: {(bucket Reject Accept).Length}"
                  line ""
                  line "## GOLD: weir accepts, F# rejects"

                  for path, src, _, _ in bucket Accept Reject do
                      line $"--- {System.IO.Path.GetFileName path}"
                      line (src.TrimEnd())

                  line ""
                  line "## F# accepts, weir rejects"

                  for path, src, _, _ in bucket Reject Accept do
                      line $"--- {System.IO.Path.GetFileName path}"
                      line (src.TrimEnd())

                  System.IO.File.WriteAllText("/tmp/corpus-report.md", report.ToString())
                  Expect.isTrue true "report written"
          } ]
