namespace ireu

type Eligibility =
    | Yes
    | No
    | Unclear

type Source =
    | FromRegex
    | FromLLM
    | Unclassified

type REU = {
    title       : string
    website     : string
    eligibility : Eligibility
    evidence    : string option
    source      : Source
    pageText    : string option
    error       : string option
}

type LLMResult =
    | Verdict of Eligibility * string option
    | Failed  of string

module REU =

    let create (title: string) (website: string) : REU = {
        title       = title
        website     = website
        eligibility = Unclear
        evidence    = None
        source      = Unclassified
        pageText    = None
        error       = None
    }

    let isUnclear (r: REU) : bool =
        r.eligibility = Unclear && r.error.IsNone
