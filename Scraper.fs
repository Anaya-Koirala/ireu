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
let private dev_email = Environment.GetEnvironmentVariable "DEV_EMAIL"

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
    c.DefaultRequestHeaders.Add("User-Agent", "iREU-Scraper/1.0 (Find REUs eligible for international students);"+dev_email)
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

/// Extract body text and relevant subpage links in a single DOM pass.
let parseHtml (baseUrl: string) (html: string) : string * string list =
    let doc  = try HtmlDocument.Parse html with _ -> HtmlDocument.Parse "<html></html>"
    let baseUri = Uri baseUrl

    let text =
        doc.Descendants "body"
        |> Seq.tryHead
        |> Option.map (fun b -> b.InnerText())
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
let scrape (reu: REU) : Async<REU> =
    async {
        match! fetchHtml reu.website with
        | None ->
            return { reu with error = Some(sprintf "Failed to fetch %s" reu.website) }
        | Some html ->
            let mainText, subpageUrls = parseHtml reu.website html

            let isConfident text =
                match classifyText text with
                | (Yes | No), _ -> true
                | Unclear, _    -> false

            let rec walk urls (acc: string list) =
                async {
                    match urls with
                    | [] -> return acc
                    | url :: rest ->
                        match! fetchHtml url with
                        | None -> return! walk rest acc
                        | Some subHtml ->
                            let subText, _ = parseHtml url subHtml
                            let acc' = subText :: acc
                            if isConfident subText
                            then return acc'
                            else return! walk rest acc'
                }

            if isConfident mainText then
                return { reu with pageText = Some mainText }
            else
                let! collected = walk subpageUrls [ mainText ]
                let combined = collected |> List.rev |> String.concat "\n\n"
                return { reu with pageText = Some combined }
    }

let scrapeAll (entries: REU list) : REU list =
    let semaphore = new Threading.SemaphoreSlim(semaphore_initial_count, semaphore_max_count)

    let throttled reu =
        async {
            do! semaphore.WaitAsync() |> Async.AwaitTask
            try
                let! result = scrape reu
                return result
            finally
                semaphore.Release() |> ignore
        }

    printfn "Scraping %d REU sites (concurrency=%d, early exit)...\n" entries.Length semaphore_max_count

    entries
    |> List.map throttled
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Array.toList
