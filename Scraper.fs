module ireu.Scraper

open System
open System.Net.Http
open FSharp.Data

open ireu
open ireu.Classifier

let private max_conn_per_server = 2
let private pooled_conn_lifetime_min = 1.0
let private timeout_sec = 15.0
let private sublink_count = 5
let private semaphore_max_count = 20
let private semaphore_initial_count = 10
let private dev_email =
    Environment.GetEnvironmentVariable "DEV_EMAIL"
    |> Option.ofObj
    |> Option.defaultValue "unknown"

let subpageKeywords =
    [
        "eligib"
        "international"
        "visa"
        "faq"
        "frequently"
        "apply"
        "require"
        "who can"
        "who should apply?"
        "qualif"
        "overview"
        "admission"
        "criteria"
        "citizen"
        "about"
        "information"
    ]

let httpClient =
    let handler = new SocketsHttpHandler()
    handler.MaxConnectionsPerServer <- max_conn_per_server
    handler.PooledConnectionLifetime <- TimeSpan.FromMinutes pooled_conn_lifetime_min

    let c = new HttpClient(handler)
    c.DefaultRequestHeaders.Add("User-Agent", $"iREU-Scraper/1.0 (Find REUs eligible for international students; %s{dev_email})")
    c.Timeout <- TimeSpan.FromSeconds timeout_sec
    c

let fetchHtml (url: string) : Async<string option> =
    async {
        try
            use! resp = httpClient.GetAsync url |> Async.AwaitTask

            if not resp.IsSuccessStatusCode then
                return None
            else
                let! html =
                    resp.Content.ReadAsStringAsync()
                    |> Async.AwaitTask
                return Some html
        with
        | _ -> return None
    }

/// Collect visible text from a node, skipping <script> and <style> subtrees.
/// DirectInnerText keeps each node's own text; recursing the child elements
/// rebuilds the full text without script/style content. Anchors are read
/// separately from the raw parse, so this never affects link discovery.
let rec private gatherText (n: HtmlNode) : string =
    match n.Name().ToLowerInvariant() with
    | "script" | "style" -> ""
    | _ ->
        let own  = n.DirectInnerText()
        let kids = n.Elements() |> List.map gatherText |> String.concat " "
        own + " " + kids

/// Extract body text and relevant subpage links in a single DOM pass.
let parseHtml (baseUrl: string) (html: string) : string * string list =
    let doc  =
        try HtmlDocument.Parse html
        with ex ->
            eprintfn "    [parse failed] %s — %s" baseUrl ex.Message
            HtmlDocument.Parse "<html></html>"
    let baseUri = Uri baseUrl

    let text =
        doc.Descendants "body"
        |> Seq.tryHead
        |> Option.map gatherText
        |> Option.defaultValue ""

    let basePath = baseUri.AbsolutePath.TrimEnd '/'

    let links =
        doc.Descendants "a"
        |> Seq.choose (fun a ->
            let href =
                a.TryGetAttribute "href"
                |> Option.map (fun x -> x.Value())

            let linkText =
                a.InnerText().ToLowerInvariant()

            match href with
            | None -> None
            | Some (h: string) ->
                let hLower = h.ToLowerInvariant()
                let relevant =
                    subpageKeywords
                    |> List.exists (fun (kw: string) -> linkText.Contains kw || hLower.Contains kw)

                if not relevant then
                    None
                else
                    try
                        let resolved = Uri(baseUri, h)

                        if
                            resolved.Host = baseUri.Host &&
                            (resolved.Scheme = "http" || resolved.Scheme = "https") &&
                            not (resolved.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        then
                            Some(resolved.ToString())
                        else
                            None
                    with
                    | _ -> None)
        |> Seq.distinct
        // Prioritise links that are in the same root page
        |> Seq.sortByDescending (
        fun url ->
            let path = Uri(url).AbsolutePath.TrimEnd '/'
            if path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) then
                1
            else
                0
        )
        |> Seq.truncate sublink_count
        |> Seq.toList

    text, links

//    1. Fetch the main page; keep its text.
//    2. If the probe is already confident (Yes/No), stop.
//    3. Otherwise fetch keyword-matched subpages one at a time, keeping
//       each page's text, and stop as soon as the probe is confident.
//    4. Store all collected text in `pageText`
//
//  Early exit means most entries need only 1-2 HTTP requests, and we
//  never hold more than one DOM tree per REU in memory at a time.
let scrape (reu: REU) : Async<REU * int> =
    async {
        match! fetchHtml reu.website with
        | None ->
            return { reu with error = Some(sprintf "Failed to fetch %s" reu.website) }, 0
        | Some html ->
            let mainText, rawSubpageUrls = parseHtml reu.website html

            // Drop links that point back to the main page itself — fetching it
            // again wastes a request and never changes the verdict.
            let baseNorm = (Uri reu.website).AbsoluteUri.TrimEnd '/'
            let subpageUrls =
                rawSubpageUrls
                |> List.filter (fun u -> (Uri u).AbsoluteUri.TrimEnd '/' <> baseNorm)

            let isConfident text =
                match classifyText text with
                | (Yes | No), _ -> true
                | Unclear, _    -> false

            let rec walk urls (acc: string list) (fetched: int) =
                async {
                    match urls with
                    | [] -> return acc, fetched
                    | url :: rest ->
                        match! fetchHtml url with
                        | None -> return! walk rest acc (fetched + 1)
                        | Some subHtml ->
                            let subText, _ = parseHtml url subHtml
                            let acc' = subText :: acc
                            if isConfident subText
                            then return acc', (fetched + 1)
                            else return! walk rest acc' (fetched + 1)
                }

            if isConfident mainText then
                return { reu with pageText = Some mainText }, 0
            else
                let! collected, fetched = walk subpageUrls [ mainText ] 0
                // Surface found-vs-visited so a silent parse failure (0 found)
                // is distinguishable from subpages that simply weren't decisive.
                printfn "    %s: %d subpage link(s) found, %d visited"
                    reu.title subpageUrls.Length fetched
                let combined = collected |> List.rev |> String.concat "\n\n"
                return { reu with pageText = Some combined }, fetched
    }

let scrapeAll (entries: REU list) : REU list =
    let semaphore = new Threading.SemaphoreSlim(semaphore_initial_count, semaphore_max_count)

    let throttled reu =
        async {
            do! semaphore.WaitAsync() |> Async.AwaitTask
            try
                let! result, subpagesVisited = scrape reu
                printfn "  (%d subpage(s)) %s" subpagesVisited reu.title
                return result
            finally
                semaphore.Release() |> ignore
        }

    printfn "Scraping %d REU sites (concurrency=%d, max subpages=%d, early exit)...\n" entries.Length semaphore_max_count sublink_count

    entries
    |> List.map throttled
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Array.toList
