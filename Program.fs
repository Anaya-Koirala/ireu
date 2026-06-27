open ireu
open ireu.Csv
open ireu.Scraper

let inputCsv  = "nsf_reusites.csv"
let outputCsv = "nsf_results.csv"
let enableLLM = true

let logResults (results: REU list) : REU list =
    let statusOf (r: REU) =
        if r.error.IsSome then "Error"
        else
            match r.eligibility with
            | Yes     -> "Yes"
            | No      -> "No"
            | Unclear -> "Unclear"
    results |> List.iter (fun r -> printfn "  [%-7s] %s" (statusOf r) r.title)
    results

let printSummary (results: REU list) =
    let count f = results |> List.filter f |> List.length
    printfn "\n=== Summary ==="
    printfn "  Total   : %d" results.Length
    printfn "  Yes     : %d" (count (fun r -> r.eligibility = Yes))
    printfn "  No      : %d" (count (fun r -> r.eligibility = No))
    printfn "  Unclear : %d" (count (fun r -> r.eligibility = Unclear && r.error.IsNone))
    printfn "  Errors  : %d" (count (fun r -> r.error.IsSome))
    printfn "  (by LLM : %d)" (count (fun r -> r.source = FromLLM))

[<EntryPoint>]
let main _ =
    let entries = readCSV inputCsv
    DotNetEnv.Env.Load() |> ignore
    printfn "Loaded %d REU entries from %s.\n" entries.Length inputCsv

    let results =
        entries
        |> scrapeAll                                        // 1. fetch page text
        |> Classifier.classifyAll                           // 2. try regex classification
        |> LLMClassifier.classifyAll enableLLM    // 3. use LLM fallback only for Unclear ones
        |> logResults

    writeCSV results outputCsv
    printSummary results
    0
