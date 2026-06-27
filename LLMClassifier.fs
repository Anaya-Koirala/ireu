module ireu.LLMClassifier

open System
open System.Net.Http
open System.Text
open System.Text.Json

open ireu

//  Used only when the regex classifier returns Unclear.
//  Sends collected page text to Gemini 2.5 Flash-Lite and asks for a
//  strict JSON verdict: yes | no | unclear, plus short evidence.

let private model = "gemini-2.5-flash-lite"
let private maxRetries = 5
let pacingMs = 4500   // ~13 req/min, under the typical free-tier 15/min cap

let private apiKey =
    Environment.GetEnvironmentVariable "GEMINI_API_KEY"
    |> Option.ofObj
    |> Option.defaultValue ""

let private endpoint =
    sprintf "https://generativelanguage.googleapis.com/v1beta/models/%s:generateContent" model

// Trim page text so we never blow past a sensible token budget.
// ~6000 chars ≈ 1500 tokens; eligibility language is almost always
// in the first part of the relevant page anyway.
let private maxChars = 12000

let private truncate (s: string) =
    if s.Length <= maxChars then s
    else s.Substring(0, maxChars)

let private systemPrompt =
    """You decide whether international students on F-1 or J-1 visas, enrolled at a US institution, are eligible for an NSF REU program, based ONLY on the page text provided.

Answer with exactly one of: "yes", "no", "unclear".

Rules, in priority order:
1. Default to "unclear". Only answer "yes" or "no" if the text contains EXPLICIT language about citizenship, residency, visa status, or international/foreign student eligibility. If it does not, you MUST answer "unclear".
2. Answer "no" if the text states the program requires US citizenship or permanent residency, or is limited to US citizens, nationals, or permanent residents.
3. Answer "yes" if the text explicitly says international or foreign students are eligible or welcome, that citizenship is not required, that eligibility is regardless of citizenship, or that the program sponsors J-1 visas.
4. A restriction that ONLY excludes foreign nationals studying at NON-US institutions implies F-1/J-1 students at US schools are eligible: answer "yes".
5. Funding source, NSF sponsorship, research topics, application deadlines, stipends, housing, and general program descriptions are NOT eligibility statements. Never base a "yes" or "no" on them. If only such text is present, answer "unclear".

The "evidence" field MUST be a verbatim quote of the specific citizenship, visa, or international-eligibility sentence you used. If you cannot quote such a sentence, you MUST answer "unclear" with evidence "".

Reply with ONLY a JSON object, no markdown, no prose:
{"eligible":"yes|no|unclear","evidence":"<verbatim quote, max 20 words, or empty>"}"""

let private httpClient =
    let c = new HttpClient()
    c.Timeout <- TimeSpan.FromSeconds 30.0
    c

/// Build the Gemini request JSON body.
let private buildBody (pageText: string) : string =
    let payload =
        {| system_instruction =
            {| parts = [| {| text = systemPrompt |} |] |}
           contents =
            [| {| role = "user"
                  parts = [| {| text = truncate pageText |} |] |} |]
           generationConfig =
            {| temperature = 0.0
               maxOutputTokens = 100
               responseMimeType = "application/json" |} |}
    JsonSerializer.Serialize payload

// The Gemini envelope contains a text part which is the verdict JSON.
// If the response is anything besides Eligibility then consider it an error (possibly rate/token limit messages)
let private parseResponse (json: string) : LLMResult =
    try
        use doc = JsonDocument.Parse json
        let text =
            doc.RootElement
               .GetProperty("candidates").[0]
               .GetProperty("content")
               .GetProperty("parts").[0]
               .GetProperty("text")
               .GetString()
            |> Option.ofObj
            |> Option.defaultValue ""

        use verdict = JsonDocument.Parse text
        let elig =
            verdict.RootElement.GetProperty("eligible").GetString()
            |> Option.ofObj
            |> Option.defaultValue ""
        let evidence =
            match verdict.RootElement.TryGetProperty "evidence" with
            | true, v -> v.GetString() |> Option.ofObj |> Option.map (fun e -> "[LLM] " + e)
            | _       -> None

        match elig.Trim().ToLowerInvariant() with
        | "yes"     -> Verdict (Yes,     evidence)
        | "no"      -> Verdict (No,      evidence)
        | "unclear" -> Verdict (Unclear, evidence)
        | other     -> Failed (sprintf "Unexpected verdict: %s" other)
    with ex ->
        Failed (sprintf "Parse error: %s" ex.Message)

let classifyWithLLM (pageText: string) : Async<LLMResult> =
    // Retry on HTTP 429 (rate limit) with exponential backoff.

    let rec attempt (n: int) : Async<LLMResult> =
        async {
            try
                let body = buildBody pageText
                use req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                req.Headers.Add("x-goog-api-key", apiKey)
                req.Content <- new StringContent(body, Encoding.UTF8, "application/json")

                use! resp = httpClient.SendAsync req |> Async.AwaitTask
                if int resp.StatusCode = 429 && n < maxRetries then
                    // back off: 2s, 4s, 8s, 16s, 32s
                    let waitMs = 2000 * pown 2 n
                    do! Async.Sleep waitMs
                    return! attempt (n + 1)
                elif not resp.IsSuccessStatusCode then
                    return Failed (sprintf "LLM API HTTP %d" (int resp.StatusCode))
                else
                    let! json = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return parseResponse json
            with ex ->
                return Failed (sprintf "LLM call failed: %s" ex.Message)
        }

    async {
        if apiKey = "" || String.IsNullOrWhiteSpace pageText then
            return Verdict (Unclear, None)
        else
            return! attempt 0
    }


let classifyOne (enabled: bool) (reu: REU) : Async<REU> =
    async {
        match reu.pageText with
        | Some text when enabled && REU.isUnclear reu ->
            match! classifyWithLLM text with
            | Verdict (Unclear, _)     -> return reu
            | Verdict (elig, evidence) -> return { reu with eligibility = elig; evidence = evidence; source = FromLLM }
            | Failed msg               -> return { reu with error = Some msg }
        | _ -> return reu
    }

let classifyAll (enabled: bool) (reus: REU list) : REU list =
    if not enabled then reus
    else
        let unclearCount = reus |> List.filter REU.isUnclear |> List.length
        printfn "LLM fallback: %d Unclear site(s) to classify..." unclearCount
        // The LLM endpoint is rate-limited, so run calls one at a time with a
        // small pacing delay rather than the wide scraper concurrency.
        let semaphore = new Threading.SemaphoreSlim(1, 1)

        let throttled reu =
            async {
                do! semaphore.WaitAsync() |> Async.AwaitTask
                try
                    let! result = classifyOne enabled reu
                    if REU.isUnclear reu then do! Async.Sleep pacingMs
                    return result
                finally
                    semaphore.Release() |> ignore
            }

        reus
        |> List.map throttled
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList
