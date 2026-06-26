module ireu.Classifier

open System
open System.Text.RegularExpressions

open ireu

let normalizeText (s: string) =
    // Single pass: replace unicode spaces then collapse whitespace
    let sb = Text.StringBuilder s.Length
    let mutable prevSpace = false

    for c in s do
        let c' =
            match int c with
            | 0x00A0
            | 0x202F
            | 0x2007
            | 0x200B -> ' '
            | _ -> c

        if Char.IsWhiteSpace c' then
            if not prevSpace then
                sb.Append ' ' |> ignore

            prevSpace <- true
        else
            sb.Append(Char.ToLowerInvariant c') |> ignore
            prevSpace <- false

    sb.ToString().Trim()

let rxOpts = RegexOptions.Singleline ||| RegexOptions.Compiled

// Gang the entire regex patterns is generated through AI all through trial and error
// This is the primary reason behind misclassification, mostly as Unclear, due to the variations in the ordering, verb and grammar
let noPatterns =
    [
      // US citizens only.
      @"(?:only|open\s+only\s+to|restricted\s+to)\s+(?:u\.?\s*s\.?|us|united\s+states).*?(?:citizens?|nationals?)"

      // Applicants must be US citizens.
      @"(?:applicants?|students?|participants?)\s+must\s+be\s+(?:a\s+)?(?:u\.?\s*s\.?|us|united\s+states)\s+(?:citizens?|nationals?)"

      // Must be US citizen or permanent resident.
      @"must\s+be\s+(?:a\s+)?(?:(?:permanent\s+resident)|(?:(?:u\.?\s*s\.?|us|united\s+states)\s+(?:citizen|national))).*?(?:or|and).*?(?:permanent\s+resident|(?:u\.?\s*s\.?|us|united\s+states)\s+(?:citizen|national))"

      // US citizenship required.
      @"(?:u\.?\s*s\.?|us|united\s+states)\s+citizenship(?:\s+or\s+permanent\s+residen\w+)?\s+is\s+required"

      // Citizen/national/permanent resident wording.
      @"(?:a\s+)?(?:u\.?\s*s\.?|us|united\s+states)\s+citizens?(?:,\s*(?:u\.?\s*s\.?|us|united\s+states)\s+nationals?)?\s*(?:,?\s*or)?\s+permanent\s+resident(?:s)?(?:\s+of\s+the\s+united\s+states(?:\s+or\s+a\s+u\.?\s*s\.?\s+territory/?possession)?)?"

      // International students excluded.
      @"international\s+students?\W+(?:are\W+)?(?:not\s+eligible|ineligible|not\s+accepted)"

      // Not open to international students.
      @"(?:not\s+open|not\s+available)\W+to\W+international\s+students?"

      // Cannot accept international students.
      @"cannot\s+accept\s+international\s+students?"

      // Only US citizens eligible.
      @"only\s+(?:u\.?\s*s\.?|us|united\s+states)\s+citizens?\W+(?:are\W+)?eligible"

      // Must be citizens/permanent residents of the US (US qualifier after noun: Clarkson form).
      @"must\s+be\s+(?:citizens?|permanent\s+residents?)\s+(?:or\s+permanent\s+residents?\s+)?of\s+the\s+(?:u\.?\s*s\.?|us|united\s+states)"

      // Can only accept US citizens [and permanent residents] (TAMU form).
      @"(?:can\s+)?only\s+accept\s+(?:u\.?\s*s\.?|us|united\s+states)\s+citizens?"

      // "citizen, national, or permanent resident of the US [or its territories]" (UHD/ETAP form).
      @"(?:citizens?|nationals?|permanent\s+residents?)(?:,\s*(?:citizens?|nationals?|permanent\s+residents?))*\s*,?\s*or\s+(?:citizens?|nationals?|permanent\s+residents?)\s+of\s+the\s+(?:u\.?\s*s\.?|us|united\s+states)"

      // No visa sponsorship.
      @"(?:no|not)\s+visa\s+sponsorship"

      // Cannot sponsor visas.
      @"cannot\s+sponsor\s+(?:a\s+)?visa"

      // Visa sponsorship unavailable.
      @"visa\s+sponsorship\s+(?:is\s+)?not\s+available"

      // No work authorization sponsorship.
      @"will\s+not\s+sponsor\s+work\s+authorization"

      // Unrestricted work authorization required.
      @"unrestricted\s+work\s+authorization\s+(?:is\s+)?required"

      // Must already have work authorization.
      @"must\s+already\s+have\s+(?:valid\s+)?work\s+authorization"
      ]
    |> List.map (fun p -> Regex(p, rxOpts))

let yesPatterns =
    [
      // International students eligible.
      @"international\s+students?\W+(?:are\W+)?(?:eligible|welcome|encouraged)"

      // Open to international students.
      @"(?:open|available)\W+to\W+international\s+students?"

      // International students may apply.
      @"international\s+students?\W+may\W+apply"

      // Citizenship not considered.
      @"(?:eligible\s+)?regardless\s+of\s+citizenship(?:\s+or\s+national\s+origin)?"

      // Non-US citizens eligible.
      @"non[\-\s]?(?:u\.?\s*s\.?|us)\s+citizens?\W+are\W+eligible"

      // F-1 holders eligible.
      @"f[\-\s]?1\s+visa\s+holders?\W+are\W+eligible"

      // J-1 holders eligible.
      @"j[\-\s]?1\s+visa\s+holders?\W+are\W+eligible"

      // Visa status irrelevant.
      @"visa\s+status\s+does\s+not\s+affect\s+eligibility"

      // J-1 sponsorship offered.
      @"(?:will\s+)?sponsor.*?j[\-\s]?1\s+visa"

      // J-1 support offered.
      @"j[\-\s]?1\s+visa\s+(?:paperwork|sponsorship|support)"

      // Foreign national advisor mentioned.
      @"foreign\s+national\s+advisor"

      // Only non-US institutions excluded.
      @"foreign\s+nationals?\W+(?:studying|enrolled|attending)\W+at\W+non[\-\s]?(?:u\.?\s*s\.?|us)\W+inst"

      // Short non-US institution wording.
      @"foreign\s+nationals?\W+at\W+non[\-\s]?(?:u\.?\s*s\.?|us)\W+inst"

      // All nationalities welcome.
      @"all\s+nationalities\s+(?:are\s+)?(?:eligible|welcome)"

      // No citizenship requirement.
      @"no\s+citizenship\s+requirement"

      // No citizenship restrictions.
      @"no\s+citizenship\s+restrictions?"

      // Immigration status irrelevant.
      @"regardless\s+of\s+immigration\s+status"

      // Citizenship not required.
      @"citizenship\s+(?:is\s+)?not\s+required"

      // Permanent residency not required.
      @"permanent\s+residen\w+\s+(?:is\s+)?not\s+required"

      // All visa holders may apply.
      @"all\s+visa\s+holders?\W+may\W+apply"

      // Applicants from any country.
      @"applicants?\s+from\s+any\s+country"

      // Any nationality accepted.
      @"students?\s+of\s+any\s+nationality"

      // Worldwide applicants accepted.
      @"(?:open|available)\s+worldwide" ]
    |> List.map (fun p -> Regex(p, rxOpts))

let classifyText (text: string) : Eligibility * string option =
    let t = normalizeText text

    let no  = noPatterns  |> List.tryFind (fun r -> r.IsMatch t)
    let yes = yesPatterns |> List.tryFind (fun r -> r.IsMatch t)

    match no, yes with
    | Some r, _    -> No,      Some(r.Match(t).Value)
    | None, Some r -> Yes,     Some(r.Match(t).Value)
    | None, None   -> Unclear, None

let classify (reu: REU) : REU =
    if reu.error.IsSome then reu
    else
        match reu.pageText |> Option.map classifyText with
        | None | Some (Unclear, _)   -> reu
        | Some (elig, evidence)      -> { reu with eligibility = elig; evidence = evidence; source = FromRegex }

let classifyAll (reus: REU list) : REU list =
    reus |> List.map classify
