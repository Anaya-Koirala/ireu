module ireu.Csv

open System.IO
open FSharp.Data
open ireu

// ============================================================
//  CSV input
// ============================================================

/// Load REU entries from a CSV with Title / Site Website columns.
let readCSV (filename: string) : REU list =
    CsvFile
        .Load(__SOURCE_DIRECTORY__ + "/" + filename, hasHeaders = true)
        .Rows
    |> Seq.map (fun row -> REU.create (row.GetColumn "Title") (row.GetColumn "Site Website"))
    |> Seq.filter (fun r -> r.website.Trim() <> "")
    |> Seq.toList

// ============================================================
//  CSV output
// ============================================================

let private eligibilityStr =
    function
    | Yes     -> "Yes"
    | No      -> "No"
    | Unclear -> "Unclear"

let private sourceStr =
    function
    | FromRegex    -> "regex"
    | FromLLM      -> "llm"
    | Unclassified -> ""

let private csvEscape (s: string) =
    let t = s.Trim()
    if t.Contains "," || t.Contains "\"" || t.Contains "\n" then
        "\"" + t.Replace("\"", "\"\"") + "\""
    else
        t

let writeCSV (results: REU list) (filename: string) =
    let csvPath = __SOURCE_DIRECTORY__ + "/" + filename
    let header = "Title,Site Website,International Eligible,Evidence,Source,Error"

    let rows =
        results
        |> List.map (fun r ->
            [ r.title
              r.website
              eligibilityStr r.eligibility
              r.evidence |> Option.defaultValue ""
              sourceStr r.source
              r.error |> Option.defaultValue "" ]
            |> List.map csvEscape
            |> String.concat ",")

    File.WriteAllLines(csvPath, header :: rows)
    printfn "Results written to %s" csvPath
