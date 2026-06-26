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

let private semaphore_max_count = 10
let private semaphore_initial_count = 5

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
    "You classify whether international students on F-1 or J-1 visas, enrolled at a US institution, are eligible for an NSF REU program based on the page text. " +
    "Rules: " +
    "If the program requires US citizenship or permanent residency, answer \"no\". " +
    "If it welcomes international students, sponsors J-1 visas, or says citizenship is not required, answer \"yes\". " +
    "If a restriction only excludes foreign nationals studying at NON-US institutions, that means F-1/J-1 students at US schools ARE eligible, so answer \"yes\". " +
    "If the text genuinely says nothing about citizenship or international eligibility, answer \"unclear\". " +
    "Reply with ONLY a JSON object, no markdown, no prose: " +
    "{\"eligible\":\"yes|no|unclear\",\"evidence\":\"<short quote from the text, max 20 words>\"}"

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
    async {
        if apiKey = "" || String.IsNullOrWhiteSpace pageText then
            return Verdict (Unclear, None)
        else
            try
                let body = buildBody pageText
                use req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                req.Headers.Add("x-goog-api-key", apiKey)
                req.Content <- new StringContent(body, Encoding.UTF8, "application/json")

                use! resp = httpClient.SendAsync req |> Async.AwaitTask
                if not resp.IsSuccessStatusCode then
                    return Failed (sprintf "LLM API HTTP %d" (int resp.StatusCode))
                else
                    let! json = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    return parseResponse json
            with ex ->
                return Failed (sprintf "LLM call failed: %s" ex.Message)
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

        let semaphore = new Threading.SemaphoreSlim (semaphore_initial_count, semaphore_max_count)

        let throttled reu =
            async {
                do! semaphore.WaitAsync() |> Async.AwaitTask
                try
                    return! classifyOne enabled reu
                finally
                    semaphore.Release() |> ignore
            }

        reus
        |> List.map throttled
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList
