# ireu - Find (US Based) REUs for International Students

- This program scrapes from a list of NSF-Funded REUs to determine eligibility for International Students.
- Currently, the result is stored in a `.csv` file.
- The data is gathered from the `nsf_reusites.csv` from the official [NSF website](https://www.nsf.gov/funding/initiatives/reu/search).
- Current REU list ~491 REUs (though not all of the pages seem to be available)
- Why F# ? because I hate myself

## Sample Results

For a random sample of 190 REUs,

| Classification               | Count |
|------------------------------|-------|
| `Yes`                        | 5     |
| `No`                         | 110   |
| `Unclear`                    | 75    |

**Warning:** The `Unclear` here includes both Page Not Found Errors and *importantly* the LLM Rate Limit Error.

## Notes and TODOs

- The classifier is limited to F-1/J-1 students **at US institutions**. Foreign nationals at *non-US* institutions might be ineligible.
- Current tests shows around half of the REUs are still classified (without LLM fallback) as unclear. This is because of the limitations of the regex patterns and the countless variations on wording, grammar and verbs. If you are able to verify the eligibility status of any REU marked as `Unclear` here, please email me with some evidence. I greatly appreciate your help. The hope is, over time, the regex pattern is more generalized and the dependency on the LLM is reduced.
- The next goal of this project is to display the results in a website with advanced filtering based on eligibility, keyword search, REU research area and location.
- Similarly, there are also a bunch of REU lists as Google Sheets online. My hope is to increase the source from the NSF's `.csv` file to those as well. If you are an international student and already have a spreadsheet of REUs you've found, PLEASE send it to me. It will be extremely helpful and I shall provide appropriate credits.
- I am having to manually download the `nsf_reusites.csv` file from NSF's website which is slow and tedious. My guess is the eligibility status is not changing soon, so running the scrape once a semester seems like a good idea.
- Even with LLM fallback, I am setback by the requests per minute and requests per day rate limits. The plan is split the classification over multiple sessions, but this also involves manual work. Again, the core assumption is that the manual work is justified because the eligibility criteria are not going to change any time soon. I have unfortunately already forked around \$10 for Gemini's Tier 1. Upgrading to Tier 2 would be the best but starts with a minimum \$100 deposit. Perhaps a donation box would help.

## How it works

```
read CSV  ->  scrape  ->  classify (regex)  ->  classify (LLM fallback)  ->  write CSV
```

1. **Scrape**: Start from the main page (under the header `Site Website` in the `.csv` file) and if needed subpages with keywords like eligibility/FAQ/details. A regex probe stops the crawl early once the answer is found, so most sites need just 1–2 requests.
2. **Classify**: Use rule-based classifier matches from a list of regex patterns and returns `Yes`, `No`, or `Unclear`. The exact wording is stored as evidence.
3. **LLM classify**:  `Unclear` sites are sent to Gemini 2.5 Flash-Lite as a fallback.

## Setup

Install `Fsharp.Data` as a dependency:

```bash
dotnet add package Fsharp.Data DotNetEnv
```

To run the program, simply:

```bash
dotnet run
```

To enable the LLM fallback, set a [Google AI Studio](https://aistudio.google.com/) key:

```bash
export GEMINI_API_KEY=your_key_here
```

Consider adding a contact email in the user-agent string:

```bash
export DEV_EMAIL=you@org.com
```

## Output

The result is stored in a `reu_results.csv` as follows:

| Column | Meaning |
|--------|---------|
| `Title` | Program title |
| `Site Website` | Program URL |
| `International Eligible` | `Yes` \| `No` \| `Unclear` |
| `Evidence` | Matched snippet (LLM verdicts prefixed `[LLM]`) |
| `Source` | `regex`, `llm`, or blank |
| `Error` | Fetch or classification error, if any |

## Contact

anaya [at] koirala [dot] xyz

[My Website](https://koirala.xyz)
