Imports System.Collections.Generic
Imports System.Text.Json
Imports System.Text.RegularExpressions

' Defensive parser for raw LLM responses. Implements spec section 2.5 in full.
'
' Per-trial pipeline (steps 1-8):
'   raw envelope JSON -> populated text field -> fence stripped ->
'   preamble discarded -> JSON parsed -> records emitted with bullet
'   markers stripped from OriginalText.
'
' Batch-level safety net (step 9) is exposed as a separate Shared method so
' the orchestrator can apply it after all per-trial parses complete.

Public NotInheritable Class LlmResponseParser

    ' Spec section 2.5 step 1: text fields probed in this order.
    Private Shared ReadOnly TextFieldNames As String() = {"text", "output", "message", "content"}

    ' Spec section 2.5 step 3: opening ```json or ``` fence, trailing ``` fence.
    Private Shared ReadOnly OpeningFencePattern As New Regex(
            "^\s*```(?:json)?\s*\r?\n?",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)
    Private Shared ReadOnly TrailingFencePattern As New Regex(
            "\r?\n?\s*```\s*$",
            RegexOptions.Compiled)

    ' Spec section 2.5 step 7: leading bullet markers on OriginalText.
    Private Shared ReadOnly BulletPattern As New Regex(
            "^\s*[*\-•·◦]\s*",
            RegexOptions.Compiled)

    ' --- JSON repair patterns (parse_invalid_json fallback) ---

    ' Model wrote a non-colon operator-like character between a quoted key
    ' and a quoted value:
    '   `"Qualifier"="elevated"`     (NCT00000718, `=`)
    '   `"OriginalText"<"12 months"` (NCT00001043, `<`)
    ' Both quotes are present on either side; only the separator is wrong.
    ' Distinct from MissingColonAfterKeyPattern below, which handles the
    ' case where the value's opening quote is also missing.
    '
    ' Character class `[=<>~]` is intentionally narrow: punctuation that
    ' could plausibly appear instead of `:` (assignment / comparison / tilde)
    ' but cannot legitimately appear between two adjacent JSON string tokens.
    ' Extend this set when a new variant turns up in production — adding a
    ' new repair pattern per character would be redundant.
    '
    ' This pattern fires FIRST so the simple operator → `:` swap happens
    ' before the more aggressive `:"` insertion that would otherwise corrupt
    ' it.
    Private Shared ReadOnly EqualsForColonPattern As New Regex(
            "(\x22\w+\x22)\s*[=<>~]\s*(\x22)",
            RegexOptions.Compiled)

    ' Compound defect: garbage inside the key's quotes AND a non-colon
    ' separator AND a value with no opening quote, all at once:
    '   `"OriginalText કરતા "> 3 months..."`     (NCT00002378)
    ' should be
    '   `"OriginalText":"3 months..."`
    ' The key's closing quote is in the right place but the model emitted
    ' tokenizer-hallucinated text (here Gujarati "than") between it and the
    ' actual key name. The separator `>` (or `<` / `=`) replaces `:` and the
    ' value content begins bare — no opening quote at all.
    '
    ' Lookbehind `(?<=[{,])` restricts the match to legitimate key positions.
    ' The garbage segment is `[^"\w][^"]*` — it MUST start with a non-word
    ' non-quote character. Without that anchor the regex would backtrack
    ' `\w+` to a shorter prefix of the key and treat the key's last letter
    ' as garbage (e.g. `"Qualifier">= 70%"` would match with `\w+ = Qualifie`
    ' and garbage = `r`, then strip the leading `>` of the value `>= 70%`
    ' as the separator — which is the MissingColonAfterKey case's job).
    ' Requiring an actual punctuation / whitespace / non-ASCII boundary
    ' between key word and garbage matches the real-world defect shape
    ' (tokenizer-hallucinated extra text starts with a space or non-ASCII
    ' char, never blends straight into the key's identifier).
    '
    ' Trailing `[^"]+"` requires the value to have at least one character —
    ' protects empty strings (`""`) from rewriting. Replacement drops the
    ' garbage and inserts the missing colon + value-opening quote.
    Private Shared ReadOnly KeyGarbageAndUnquotedValuePattern As New Regex(
            "(?<=[{,])\s*""(\w+)(?:[^""\w][^""]*)""\s*[><=]\s*([^""]+)""",
            RegexOptions.Compiled)

    ' Stricter cousin of EqualsForColon: model dropped BOTH the key's closing
    ' quote AND used `=` instead of `:` — `"Qualifier="no clinical evidence"`
    ' (NCT00003906) where well-formed JSON would be
    ' `"Qualifier":"no clinical evidence"`.
    '
    ' Lookbehind `(?<=[{,])` requires the match to start at a position where
    ' a key is legitimately expected (object open or field separator), so the
    ' regex cannot fire inside a value that happens to contain `"word="`
    ' substring — well-formed JSON values cannot contain a raw `"`, and even
    ' string-array elements like `["a=b"]` are protected because `[` is not
    ' in the lookbehind set. `\w+` is the key name; the trailing `"` is the
    ' value's opening quote. Replacement re-inserts the key's missing closing
    ' quote and the `:` separator: `"key="` → `"key":"`.
    Private Shared ReadOnly MalformedKeyEqualsPattern As New Regex(
            "(?<=[{,])\s*""(\w+)\s*=\s*""",
            RegexOptions.Compiled)

    ' Model dropped the colon AND the value's opening quote after a key.
    ' We've observed:
    '   `"Qualifier">= 70%"`   (NCT00000105, value starts with >)
    '   `"Concept"(A-a) DO2"`  (NCT00000717, value starts with `(`)
    ' Repair inserts `:"` between the key's closing quote and the value
    ' content; the value's closing quote is usually already present.
    '
    ' Negative lookahead means: "a quoted identifier NOT immediately followed
    ' by a valid JSON continuation (`:`, `,`, `]`, `}`, allowing whitespace
    ' before any of them)". Catches whatever character the model dropped
    ' the separator before — operators, parens, digits, etc. — without
    ' touching properly-formed keys.
    Private Shared ReadOnly MissingColonAfterKeyPattern As New Regex(
            """([A-Za-z_][A-Za-z0-9_]*)""(?!\s*[:,\]\}])",
            RegexOptions.Compiled)

    ' Trailing commas before `]` or `}` — JSON5 accepts these, strict JSON
    ' doesn't. Some models emit them. `[1, 2,]` → `[1, 2]`.
    Private Shared ReadOnly TrailingCommaPattern As New Regex(
            ",(\s*[\]\}])",
            RegexOptions.Compiled)

    ' Model dropped the `"NCT_ID":` key for one record in an array, leaving
    ' the bare NCT ID where a key:value pair was expected:
    '   `{"NCT00000439","Criterion":"Exclusion",...}`   (NCT00000439)
    ' Every other record in the same array uses the correct shape
    ' `{"NCT_ID":"NCT00000439","Criterion":...}`; the model just forgot the
    ' key on one. The lookahead `(?=\s*,)` requires the bare ID to be
    ' followed by `,` (i.e. used as a value where a key-value pair was
    ' expected) — a legitimate `"NCT_ID":"NCT..."` always has `:` before
    ' the ID, so this regex never sees it (the `{` lookbehind on the
    ' opening brace makes sure we only touch positions where a record
    ' object starts). `NCT\d{8,}` matches the AACT identifier shape; we
    ' don't try to detect arbitrary bare-string-as-key cases because
    ' that would risk corrupting legitimate single-value emissions.
    Private Shared ReadOnly MissingNctIdKeyPattern As New Regex(
            "\{\x22(NCT\d{8,})\x22(?=\s*,)",
            RegexOptions.Compiled)

    ' Model emitted a schema key with NO `:value` after it — the colon and
    ' value were both dropped, leaving an orphan key sitting between two
    ' well-formed key:value pairs:
    '   `"Domain":"Disease","Concept","Qualifier":"x"` (NCT00053560)
    ' Repair inserts an empty-string value so the field round-trips as empty
    ' rather than failing the whole record.
    '
    ' Lookbehind `[{,]\s*` constrains to object-key positions (start of object
    ' or right after a separator). Lookahead `[,}]` requires the orphan to be
    ' followed by structural continuation rather than `:` (the well-formed
    ' shape). The key-name whitelist further restricts matches to our seven
    ' schema fields so the regex cannot fire on string-array elements or
    ' content inside escaped strings in some value, even if their wrapping
    ' chars happen to line up with the lookbehind / lookahead.
    Private Shared ReadOnly OrphanKeyMissingValuePattern As New Regex(
            "(?<=[{,]\s*)""(NCT_ID|Criterion|Domain|Concept|Qualifier|TimeWindow|OriginalText)""(?=\s*[,}])",
            RegexOptions.Compiled)

    ' Model emitted an extra closing `"` after a properly-closed string value:
    '   `"pregnant or lactating""}`  (NCT00000284, expected `"pregnant or lactating"}`)
    ' The first `"` is the legitimate value close; the second `"` is spurious.
    '
    ' We must NOT touch legitimate empty strings (`"Qualifier":""}` where the
    ' `""` is intentional) or escaped quotes inside string values
    ' (`"He said \"hello\""` — the `\"` followed by closing `"` looks like
    ' consecutive quotes but the first is escaped).
    '
    ' Lookbehind `[^:"\s\\]` says: only match when the first of the `""` pair
    ' is preceded by content (not `:` for empty-string case, not another `"`
    ' for triple-quote oddities, not whitespace, not `\` for escaped-quote
    ' case). Lookahead requires the pair to be followed by structural JSON
    ' continuation (`,` `]` `}` with optional whitespace), so we only strip
    ' when the model clearly meant to close out of the value.
    Private Shared ReadOnly ExtraClosingQuotePattern As New Regex(
            "(?<=[^:""\s\\])""""(?=\s*[,\]\}])",
            RegexOptions.Compiled)

    ' Model emitted a properly-closed value followed by unquoted commentary
    ' and an "alternative" quoted string before the structural terminator:
    '   "OriginalText":"x* OR y*" restated: "x"}   (NCT00004907)
    ' The first quoted string IS the value the model emitted; the
    ' "restated:" / "alternatively:" / etc. trailer is editorial commentary
    ' that breaks JSON. Repair keeps the original key:value (preserving what
    ' the LLM actually said) and strips the garbage + spurious second string
    ' up to the next `,` or `}`.
    '
    ' Capture group 1 is the legit `":"value"` slice (key terminator through
    ' value close); replacement re-emits just that group, dropping everything
    ' after it. Pattern requires:
    '   - `":` to anchor inside a key:value pair (not an array element)
    '   - at least one whitespace BEFORE the garbage (so adjacent fields with
    '     missing commas like `"a":"b""c":"d"` don't false-positive)
    '   - at least one non-structural non-quote char of garbage
    '   - a SECOND fully-formed quoted string
    '   - lookahead for `,` or `}` so we only strip when the next thing is
    '     a legitimate structural continuation
    Private Shared ReadOnly TrailingCommentaryAfterValuePattern As New Regex(
            "("":\s*""[^""\\]*(?:\\.[^""\\]*)*"")\s+[^,}""\n]+""[^""\\]*(?:\\.[^""\\]*)*""(?=\s*[,\}])",
            RegexOptions.Compiled)

    ' Model leaked a tokenizer escape sequence (`\n` / `\t` / etc.) between
    ' two array records, OUTSIDE any string context:
    '   `},  \n{   "NCT_ID":"NCT00053495",...`   (NCT00053495)
    ' JSON only permits these escapes inside string values; outside, even
    ' `\n` is a parse error. We don't try to be smart — strip any literal
    ' `\<word>` that appears between `},` and the next `{`.
    Private Shared ReadOnly BareEscapeBetweenRecordsPattern As New Regex(
            "\}\s*,\s*\\\w+\s*\{",
            RegexOptions.Compiled)

    ' Model glued a lowercase `n` directly onto the front of a schema key:
    '   `"nNCT_ID":"NCT00053495"`   (NCT00053495, twice in the same response)
    ' Almost certainly the same tokenizer leak that produces the
    ' BareEscapeBetweenRecords / OrphanStringBeforeNctId shapes — a `\n`
    ' escape leaking out — but here the orphan got fused into the key's
    ' quotes with no separator.
    '
    ' Lookbehind `[{,]\s*` keeps the match to object-key positions. The
    ' schema-key whitelist is the strict safety net: every schema key
    ' begins with uppercase, so `"n<UpperCaseKey>"` is unambiguously a
    ' defect — there's no legitimate `"nNCT_ID"` etc. in our payloads.
    Private Shared ReadOnly NPrefixedSchemaKeyPattern As New Regex(
            "(?<=[{,]\s*)""n(NCT_ID|Criterion|Domain|Concept|Qualifier|TimeWindow|OriginalText)""\s*:",
            RegexOptions.Compiled)

    ' Model emitted an orphan quoted string between the opening `{` and the
    ' first real key — the spurious string carries no semantic value:
    '   `{   "n{"NCT_ID":"NCT00053495",...`   (NCT00053495)
    ' The lookbehind constrains to immediately-after-`{` and the lookahead
    ' constrains to immediately-before `"NCT_ID":`, so the pattern can only
    ' fire on schema-first-key positions. One or more orphan strings (with
    ' optional whitespace between them) all get stripped in a single pass.
    ' Backtracking guarantees the real `"NCT_ID"` is NOT consumed as part
    ' of the orphan run — the lookahead forces the engine to back off to
    ' leave `"NCT_ID":` intact.
    Private Shared ReadOnly OrphanStringBeforeNctIdPattern As New Regex(
            "(?<=\{)\s*(""[^""]*""\s*)+(?=""NCT_ID""\s*:)",
            RegexOptions.Compiled)

    ' Model fused a stray record-separator fragment `"},{` onto the front of
    ' an object, before its first real key:
    '   `{   "},{"NCT_ID":"NCT00048230",...`   (NCT00048230)
    ' should be
    '   `{"NCT_ID":"NCT00048230",...`
    ' The `"},{` looks like a duplicated record boundary — close-value /
    ' close-object / separator / open-object — leaked into the next object.
    '
    ' Distinct from OrphanStringBeforeNctId: there the orphan is one or more
    ' *complete* quoted strings (even quote count); here the fragment carries
    ' an odd number of quotes, so that pattern's `(""[^""]*"")+` cannot match
    ' it without consuming the real `"NCT_ID"` opening quote.
    '
    ' Anchoring is what makes this safe: the literal `{` start plus the
    ' `"NCT_ID":` lookahead pin the match to a record-object boundary, and
    ' `{"},{"NCT_ID":` can never be valid JSON — after `{`, `"},{"` could only
    ' be a key string, but then `:` (not `NCT_ID`) would have to follow — so
    ' the pattern only ever fires on already-broken input. Replacement drops
    ' the fragment, leaving `{"NCT_ID":`.
    Private Shared ReadOnly StrayRecordSeparatorBeforeNctIdPattern As New Regex(
            "\{\s*\x22\},\{(?=\s*\x22NCT_ID\x22\s*:)",
            RegexOptions.Compiled)

    ' Model wrapped one or more records in a stray `"` right before the
    ' record-opening `{`, as if it had started stringifying a block of JSON and
    ' then stopped:
    '   `},  "{"NCT_ID":...onset"},{"NCT_ID":...patients"},"{   "NCT_ID":...`
    '                                                              (NCT07275749)
    ' Here a bare `"` sits immediately before `{"NCT_ID"` in two places (before
    ' the first wrapped record, and before the next real record). The parser
    ' reads `"{"` as the string `{`, then chokes on the bare `NCT_ID` that
    ' follows. Dropping each stray `"` turns the wrapped records back into
    ' ordinary comma-separated array elements.
    '
    ' Safe because a `"` immediately followed by `{ ... "NCT_ID"` is never valid
    ' JSON: a record-opening `{` is only ever preceded by `[`, `,`, or `:`
    ' (array element / object value), and a string-value's closing `"` is always
    ' followed by `,` `]` `}` or `:` - never `{`. So `"{` at a record boundary
    ' only appears in already-broken input. The `"NCT_ID"` lookahead pins the
    ' match to a record start (matching the sibling NCT_ID-boundary patterns)
    ' and `\s*` lets the brace be compact (`"{"NCT_ID"`) or pretty-printed
    ' (`"{` newline `  "NCT_ID"`). A global replace clears every stray wrapper.
    Private Shared ReadOnly StrayQuoteBeforeRecordPattern As New Regex(
            "\x22(?=\{\s*\x22NCT_ID\x22)",
            RegexOptions.Compiled)

    ' Model dropped the `,` separator between two complete records, leaving the
    ' previous object's `}` butted straight against the next object's `{`:
    '   `..."}{   "NCT_ID":...`   (NCT07088458, twice mid-array)
    ' Every record is whole; only the comma is gone. Distinct from
    ' TruncatedTrailingRecord, which only fixes a `}{` anchored at end-of-input
    ' (a truncated partial record); this fires on `}{` boundaries IN THE MIDDLE
    ' of the array, where a complete record follows. The two overlap on a
    ' single `}{` whose trailing record is cut off at EOF, so this pass MUST run
    ' AFTER TruncatedTrailingRecord - that pattern strips the partial record
    ' first, leaving only genuine complete-record boundaries for this one.
    '
    ' The `"NCT_ID"` lookahead pins the match to a genuine record boundary (the
    ' next object's first key), exactly like the sibling NCT_ID-boundary
    ' patterns - so a `}{` that happened to sit inside a string value can't
    ' match (it would not be followed by `"NCT_ID"`). A correct `},{` boundary
    ' is never touched: `\}\s*\{` requires whitespace-only between the braces,
    ' but a real separator has a `,` there, which `\s*` cannot consume. `\s*`
    ' covers both compact (`}{`) and pretty-printed (`}` newline `{`) gaps.
    Private Shared ReadOnly MissingCommaBetweenRecordsPattern As New Regex(
            "\}\s*\{(?=\s*\x22NCT_ID\x22)",
            RegexOptions.Compiled)

    ' Model emitted records as STRINGIFIED (double-encoded) JSON array elements
    ' instead of real objects - each element is a quoted string whose content is
    ' an escaped object:
    '   `"{\"NCT_ID\":\"NCT06689254\",...,\"OriginalText\":\"...\"}"`  (NCT06689254)
    ' A clean run of these is valid heterogeneous JSON (EmitRecords recovers
    ' them - see TryBuildRecordFromStringElement), but here the LAST element is
    ' unterminated (`...\"}]` - the closing wrapping `"` was dropped), so the
    ' whole document fails to parse. This repair decodes each stringified
    ' element back into a real object so the array parses.
    '
    ' The token is `"` + `{` + (escaped char | non-quote-non-backslash)* + `}`
    ' + an OPTIONAL trailing `"`. Real objects (no wrapping `"`) and the stray-
    ' quote-wrapped shape (`"{"NCT_ID"...`, unescaped quotes) can't match: after
    ' `{` the next char there is a bare `"`, which stops the run before any `}`.
    ' The optional trailing `"` lets the unterminated final element match too;
    ' DecodeStringifiedRecordElement appends the missing `"` before decoding.
    ' The evaluator only rewrites a token that decodes to a JSON object carrying
    ' an NCT_ID, so an ordinary value that merely starts with `{` is left alone.
    Private Shared ReadOnly StringifiedRecordElementPattern As New Regex(
            "\x22\{(?:\\.|[^\x22\\])*\}\x22?",
            RegexOptions.Compiled)

    ' Model closed the JSON string BEFORE it closed a parenthetical inside
    ' the value:
    '   `"...with fistula")`   (NCT00007839, should be `"...with fistula)"`)
    ' Result: a stray `)` between the value's closing `"` and the next
    ' structural char (`,` or `}`). JSON parse fails because nothing valid
    ' can sit between a value and the next field separator.
    '
    ' Repair: move the orphan paren(s) BACK inside the closing quote so the
    ' parenthetical's meaning is preserved. Lookahead `[,}]` keeps the
    ' match constrained to value-end positions; `[^"]*` excludes `"` so the
    ' regex can't cross a JSON string boundary mid-match. Doesn't fire on
    ' `"foo)bar"` (legitimate paren inside the value, no orphan after the
    ' closing quote) — the `\)+` requires at least one paren immediately
    ' after the closing `"`.
    Private Shared ReadOnly StrayClosingParenAfterValuePattern As New Regex(
            "(""[^""]*)""(\)+)(?=\s*[,}])",
            RegexOptions.Compiled)

    ' Model left unescaped `"` characters INSIDE a string value — JSON requires
    ' interior quotes to be written `\"`, but the model copied a parenthetical
    ' or quoted term verbatim:
    '   `"OriginalText":"...intervention ("Synbiotic Supplement" or placebo)..."`
    '                                                              (NCT07605728)
    ' System.Text.Json ends the value at the first bare `"` (here right after
    ' `intervention (`) and then fails on the trailing garbage.
    '
    ' We can only locate the value's TRUE boundaries by anchoring on the schema:
    ' the match starts at a known schema key (`"<Key>":"`) and the value's real
    ' closing quote is the one immediately followed by a legitimate structural
    ' continuation — either `,"<NextSchemaKey>"` (another field follows) or a
    ' `}` / `]` (record / array end; OriginalText, the field this defect hits
    ' most, is always last). The lazy `[\s\S]*?` therefore skips over every
    ' interior `"` (none of which are followed by that continuation) and stops
    ' only at the genuine value terminator. CollapseRawNewlines runs after this,
    ' so the captured value may legitimately span physical lines here.
    '
    ' Anchoring on the schema key + structural lookahead is what makes the
    ' aggressive `[\s\S]*?` safe: a well-formed value never carries an
    ' unescaped interior `"` (it would be `\"`), so EscapeInteriorQuotesInValue
    ' is a byte-for-byte no-op on every already-valid field and only rewrites a
    ' value that actually contains a bare quote. Requiring the next token to be
    ' a SCHEMA key (not just any `"`) keeps clinical text like
    ' `("term" or other)` from being mistaken for a field boundary.
    '
    ' This runs LATE in TryRepairJson (after the structural fixes, before the
    ' newline collapse), so it cannot pre-empt MissingColonAfterKey /
    ' TrailingCommentaryAfterValue on the shapes those patterns own. The price
    ' is that a SINGLE-WORD bare interior quote (`the "active" arm`) is claimed
    ' by MissingColonAfterKey first - `"active"` looks exactly like a key whose
    ' colon was dropped. The observed defect (NCT07605728) is multi-word
    ' (`"Synbiotic Supplement"`, with a space, so `"word"` can't match it), so
    ' it falls through to here intact. A single-word interior quote remains a
    ' known gap - genuinely ambiguous with the dropped-colon defect.
    Private Shared ReadOnly SchemaValueWithInteriorQuotesPattern As New Regex(
            "\x22(NCT_ID|Criterion|Domain|Concept|Qualifier|TimeWindow|OriginalText)\x22\s*:\s*\x22([\s\S]*?)\x22(?=\s*(?:,\s*\x22(?:NCT_ID|Criterion|Domain|Concept|Qualifier|TimeWindow|OriginalText)\x22|[}\]]))",
            RegexOptions.Compiled)

    ' A `"` that is NOT preceded by a backslash — i.e. an unescaped interior
    ' quote that must become `\"`. The negative lookbehind leaves already-
    ' escaped `\"` sequences untouched, so re-running over valid content is a
    ' no-op.
    Private Shared ReadOnly UnescapedQuoteInContentPattern As New Regex(
            "(?<!\\)\x22",
            RegexOptions.Compiled)

    ' Response truncated mid-record — the last well-formed `}` is followed
    ' by a partial new record `{...` that never closes, and the array's
    ' final `]` is missing:
    '   `...}, {"NCT_ID":"NCT0", ...]<EOF>`  (mid-field truncation)
    '   `...}{`                              (NCT00030147, immediate)
    ' Strip the partial record and close the array with `]`. The pattern's
    ' `[^}]*` cannot cross a `}` so it only consumes the tail run up to
    ' end-of-input — the regex can't match a `}{` that sits INSIDE the
    ' array (e.g. inside a string value or between two complete records
    ' with no separator), only one anchored to the end of the document.
    Private Shared ReadOnly TruncatedTrailingRecordPattern As New Regex(
            "\}\s*\{[^}]*$",
            RegexOptions.Compiled)

    ' Model emitted a stray closing `}` after the root array's `]` — the array
    ' itself is complete and well-formed, but a duplicate object-closer trails
    ' it:
    '   `...}` newline `]` newline `}`   (NCT07299487)
    ' System.Text.Json is strict about trailing content: once the root array
    ' closes, any non-whitespace after it throws ("additional text after the
    ' JSON value"), so the otherwise-perfect array never parses.
    '
    ' We only strip a trailing `}` (the observed char) that sits after a literal
    ' `]` at end-of-input. Our payloads are array-rooted (the prompt mandates a
    ' JSON array), so a `}` after the root `]` can only be stray. An object-
    ' rooted payload would have to end `...]}` with a BARE `]` right before the
    ' brace to be touched here, but our schema has no array-valued fields, so a
    ' record always closes `..."}` (a quote, not `]`) before the object's `}` —
    ' the `\]` anchor can't match that. `\}+` collapses repeated stray closers.
    Private Shared ReadOnly StrayTrailingBraceAfterArrayPattern As New Regex(
            "\]\s*\}+\s*$",
            RegexOptions.Compiled)

    ' Model emitted a complete run of records but forgot the array's closing
    ' `]` — the response ends on the last record's `}` with the array still
    ' open:
    '   `[{...},{...},{...}` newline EOF   (NCT07295392)
    ' System.Text.Json reaches end-of-input inside the array and throws. This
    ' is NOT the TruncatedTrailingRecord shape (there a partial `{...` trails
    ' the last good `}`); here every record is whole and only the `]` is gone.
    '
    ' The fix only fires when the text both STARTS with `[` (array root — our
    ' prompt mandates a JSON array; a bare-object root starts with `{` and is
    ' left alone) and ENDS on a `}` (a complete final record). A response
    ' truncated mid-record ends on a value char / `,` / `:` instead, so `\}\s*$`
    ' won't match and we never fabricate a close for genuinely incomplete data.
    ' Runs AFTER StrayTrailingBraceAfterArray so a `[...]}` (already-closed
    ' array plus stray brace) is reduced to `[...]` first and can't be mistaken
    ' for an unterminated array here. Group 1 captures `[` ... last `}`.
    Private Shared ReadOnly UnterminatedArrayPattern As New Regex(
            "^(\s*\[[\s\S]*\})\s*$",
            RegexOptions.Compiled)

    ' Model closed the LAST record with the array's `]` instead of the object's
    ' `}` — the final object never gets its closing brace, the `]` that should
    ' have followed it stands in for it:
    '   `..."OriginalText":"...risk."` newline `  ]`   (NCT06692166)
    ' should be `..."...risk."` newline `  }]`. The parser, still inside the
    ' object, hits `]` where it expects `,` or `}` and throws. This is the
    ' mirror of UnterminatedArray (which ends on `}` with the `]` missing); here
    ' the text ends on `]` with the object's `}` missing. Repair inserts the
    ' `}` between the last value and the `]`.
    '
    ' Safe because our root array holds OBJECTS: a well-formed array always ends
    ' `..."}]` (value-close, object-close, array-close), so a value-closing `"`
    ' sits directly before `]` only when the `}` was dropped. The `\s*$` anchor
    ' restricts the match to the array terminator, and requiring a closing `"`
    ' before the `]` means a record truncated mid-value (no closing quote) is
    ' left alone rather than force-closed.
    Private Shared ReadOnly LastRecordClosedByArrayBracketPattern As New Regex(
            "\x22\s*\]\s*$",
            RegexOptions.Compiled)

    ' Invalid JSON escape sequences inside string values — the model copies
    ' AACT's markdown-escape backslashes (e.g. "\>" for "\>/=") verbatim into
    ' its JSON output. JSON only allows \", \\, \/, \b, \f, \n, \r, \t,
    ' \uXXXX after a `\`; anything else throws JsonException.
    '
    ' Negative lookbehind `(?<!\\)` ensures we don't damage a properly-escaped
    ' `\\X` sequence: in that case the first `\` is preceded by nothing or
    ' a non-backslash, the second `\` is preceded by a backslash, so the
    ' regex skips both.
    Private Shared ReadOnly InvalidJsonEscapePattern As New Regex(
            "(?<!\\)\\([^""\\/bfnrtu])",
            RegexOptions.Compiled)

    ' A complete JSON string token: opening `"`, content (any run of chars
    ' that are neither a bare `"` nor `\`, interleaved with `\`-escapes),
    ' closing `"`. `[^""\\]` matches raw CR/LF, so a value the model wrote
    ' across multiple physical lines is still captured as one whole token.
    ' Paired with CollapseRawNewlinesInString — see the repair note there.
    Private Shared ReadOnly JsonStringTokenPattern As New Regex(
            "\x22[^\x22\\]*(?:\\.[^\x22\\]*)*\x22",
            RegexOptions.Compiled)

    ' A run of whitespace containing at least one raw CR/LF — used to
    ' collapse a model-introduced line break (and its indentation) inside a
    ' string value down to a single space.
    Private Shared ReadOnly RawNewlineRunPattern As New Regex(
            "\s*[\r\n]+\s*",
            RegexOptions.Compiled)

    ''' <summary>
    ''' Parses one LLM response envelope into zero or more criterion records.
    ''' Convenience wrapper over <see cref="ParseWithOutcome"/> for callers
    ''' that don't need the parse outcome (e.g. unit-test assertions on the
    ''' record list). The orchestrator uses ParseWithOutcome so it can
    ''' distinguish "LLM returned [] cleanly" from "parser couldn't read the
    ''' LLM's output".
    ''' </summary>
    Public Function Parse(rawResponse As String, trialNctId As String) As IReadOnlyList(Of CriterionRecord)
        Return ParseWithOutcome(rawResponse, trialNctId).Records
    End Function

    ''' <summary>
    ''' Parses one LLM response envelope and reports the outcome alongside
    ''' the records — Success when one or more records came out, EmptyArray
    ''' when valid JSON resolved to no records, InvalidJson when parsing
    ''' could not produce anything usable (truncated by max_tokens, malformed,
    ''' or empty payload).
    ''' </summary>
    ''' <param name="rawResponse">
    ''' Raw envelope JSON. The text payload may live in any of the fields
    ''' { text, output, message, content }. Non-JSON input is treated as the
    ''' text payload directly.
    ''' </param>
    ''' <param name="trialNctId">
    ''' The trial whose response this is. Used as a fallback when the LLM omits
    ''' NCT_ID on a record; the LLM's value is preserved when present (it might
    ''' disagree with the parameter, but second-guessing it is out of scope here).
    ''' </param>
    Public Function ParseWithOutcome(rawResponse As String, trialNctId As String) As LlmParseResult

        ' Step 2: skip null/empty/whitespace responses (failed calls).
        If String.IsNullOrWhiteSpace(rawResponse) Then
            Return New LlmParseResult(Array.Empty(Of CriterionRecord)(), LlmParseResult.OutcomeInvalidJson)
        End If

        ' Step 1: extract the populated text field from the envelope.
        Dim text = ExtractTextField(rawResponse)
        If String.IsNullOrWhiteSpace(text) Then
            Return New LlmParseResult(Array.Empty(Of CriterionRecord)(), LlmParseResult.OutcomeInvalidJson)
        End If

        ' Step 3: strip code fences.
        text = OpeningFencePattern.Replace(text, "")
        text = TrailingFencePattern.Replace(text, "")

        ' Step 4: discard preamble before the first '[' or '{'.
        Dim firstBracket = FindFirstBracket(text)
        If firstBracket < 0 Then
            Return New LlmParseResult(Array.Empty(Of CriterionRecord)(), LlmParseResult.OutcomeInvalidJson)
        End If
        text = text.Substring(firstBracket)

        ' Step 5: parse JSON. JsonException → fall through to the repair pass;
        ' if repair also fails, return invalid_json. Spec section 6.2 used to
        ' mandate "silently drop the row"; we now distinguish via the outcome
        ' so the audit row records what actually went wrong.
        Dim first = TryParseOnce(text, trialNctId, wasRepaired:=False)
        If first IsNot Nothing Then Return first

        Dim repaired = TryRepairJson(text)
        If repaired IsNot Nothing AndAlso Not String.Equals(repaired, text, StringComparison.Ordinal) Then
            Dim retry = TryParseOnce(repaired, trialNctId, wasRepaired:=True)
            If retry IsNot Nothing Then Return retry
        End If

        Return New LlmParseResult(Array.Empty(Of CriterionRecord)(), LlmParseResult.OutcomeInvalidJson)
    End Function

    ' Single best-effort parse attempt. Returns Nothing on JsonException so
    ' the caller can try a repair pass. WasRepaired surfaces on the result
    ' as an observability signal — orchestrator logs a warning when set.
    Private Shared Function TryParseOnce(text As String, trialNctId As String, wasRepaired As Boolean) As LlmParseResult
        Try
            Using doc = JsonDocument.Parse(text)
                Dim records = EmitRecords(doc.RootElement, trialNctId)
                Dim outcome As String = If(records.Count > 0,
                                            LlmParseResult.OutcomeSuccess,
                                            LlmParseResult.OutcomeEmptyArray)
                Return New LlmParseResult(records, outcome, wasRepaired)
            End Using
        Catch ex As JsonException
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' Best-effort fixer for common LLM-emitted JSON mistakes. Conservative —
    ''' only patches patterns we've actually observed in production audit
    ''' rows. Bounded by one regex pass per pattern; if the input is too
    ''' broken for these targeted fixes the parser falls through to
    ''' invalid_json. Returns Nothing if no repair applied.
    ''' </summary>
    Friend Shared Function TryRepairJson(text As String) As String
        If String.IsNullOrEmpty(text) Then Return Nothing

        ' "{\"NCT_ID\":...}"  →  {"NCT_ID":...}   (decode stringified record
        ' elements back into real objects). Runs FIRST: once decoded the text is
        ' clean JSON, so the structural passes below see ordinary objects. No-op
        ' on every token that does not decode to a record object.
        Dim repaired = StringifiedRecordElementPattern.Replace(
                text, AddressOf DecodeStringifiedRecordElement)

        ' "key"="value"  →  "key":"value"   (run first so the next pattern
        ' doesn't try to insert `:"` and corrupt the already-quoted value)
        repaired = EqualsForColonPattern.Replace(repaired, "$1:$2")

        ' "key="value"  →  "key":"value"   (key's closing quote AND `:`
        ' both missing — runs right after EqualsForColon because the two
        ' patterns describe variants of the same defect)
        repaired = MalformedKeyEqualsPattern.Replace(repaired, """$1"":""")

        ' "key garbage">value"  →  "key":"value"   (garbage inside the
        ' key's quotes, wrong separator, value missing opening quote — runs
        ' before MissingColonAfterKey so its more aggressive `:"` insertion
        ' doesn't fire on the already-broken shape)
        repaired = KeyGarbageAndUnquotedValuePattern.Replace(repaired, """$1"":""$2""")

        ' "key">= 70%"  →  "key":">= 70%"
        repaired = MissingColonAfterKeyPattern.Replace(repaired, """$1"":""")

        ' "Concept",  →  "Concept":"",   (orphan schema key with no :value;
        ' insert an empty-string value so the rest of the record survives)
        repaired = OrphanKeyMissingValuePattern.Replace(repaired, """$1"":""""")

        ' },  \n{  →  },{   (bare escape sequence leaked between records)
        repaired = BareEscapeBetweenRecordsPattern.Replace(repaired, "},{")

        ' {   "n{"NCT_ID":...  →  {"NCT_ID":...   (orphan quoted string
        ' between { and the first real key)
        repaired = OrphanStringBeforeNctIdPattern.Replace(repaired, "")

        ' {   "},{"NCT_ID":...  →  {"NCT_ID":...   (stray record-separator
        ' fragment fused onto the front of the next object)
        repaired = StrayRecordSeparatorBeforeNctIdPattern.Replace(repaired, "{")

        ' "{"NCT_ID":...  →  {"NCT_ID":...   (stray `"` wrapping a record's
        ' opening brace)
        repaired = StrayQuoteBeforeRecordPattern.Replace(repaired, "")

        ' "nNCT_ID":  →  "NCT_ID":   (tokenizer leak fused into key name)
        repaired = NPrefixedSchemaKeyPattern.Replace(repaired, """$1"":")

        ' ...}{    →  ...}]    (truncated mid-record; strip the partial
        ' trailing record and close the array)
        repaired = TruncatedTrailingRecordPattern.Replace(repaired, "}]")

        ' }{"NCT_ID":...  →  },{"NCT_ID":...   (missing comma between two
        ' complete records mid-array). MUST run after TruncatedTrailingRecord:
        ' a `}{` whose trailing record is truncated at EOF is the truncation
        ' defect (strip it), so let that pattern claim it first; only a `}{`
        ' with a COMPLETE record after survives to here and needs the comma.
        repaired = MissingCommaBetweenRecordsPattern.Replace(repaired, "},{")

        ' ...]}  →  ...]    (stray `}` trailing the complete root array)
        repaired = StrayTrailingBraceAfterArrayPattern.Replace(repaired, "]")

        ' [{...},{...}  →  [{...},{...}]   (model forgot the array's closing `]`;
        ' all records complete, only the bracket missing)
        repaired = UnterminatedArrayPattern.Replace(repaired, "$1]")

        ' ..."]  →  ..."}]   (last record closed by the array `]` instead of the
        ' object `}`; insert the missing brace)
        repaired = LastRecordClosedByArrayBracketPattern.Replace(repaired, """}]")

        ' "...fistula")  →  "...fistula)"   (orphan `)` outside the
        ' value's closing quote; move it back inside)
        repaired = StrayClosingParenAfterValuePattern.Replace(repaired, "$1$2""")

        ' {"NCT00000439",  →  {"NCT_ID":"NCT00000439",   (dropped key on
        ' one record where the bare NCT ID was emitted as a value)
        repaired = MissingNctIdKeyPattern.Replace(repaired, "{""NCT_ID"":""$1""")

        ' value""}  →  value"}    (extra closing quote after a value;
        ' lookbehind protects against collapsing legit empty strings)
        repaired = ExtraClosingQuotePattern.Replace(repaired, """")

        ' "key":"val" restated: "alt"}  →  "key":"val"}
        ' (model appended unquoted commentary + alternative quoted string)
        repaired = TrailingCommentaryAfterValuePattern.Replace(repaired, "$1")

        ' [a, b,]  →  [a, b]    {k:v,}  →  {k:v}
        repaired = TrailingCommaPattern.Replace(repaired, "$1")

        ' \>  →  >    (markdown-escape backslashes copied from AACT criteria)
        repaired = InvalidJsonEscapePattern.Replace(repaired, "$1")

        ' "...("Synbiotic Supplement" or placebo)..."  →  escaped \" interior
        ' quotes. Runs after the structural fixes (so quotes are otherwise
        ' balanced) and before the newline collapse (which needs intact string
        ' tokens). No-op on every value that has no bare interior quote.
        repaired = SchemaValueWithInteriorQuotesPattern.Replace(
                repaired, AddressOf EscapeInteriorQuotesInValue)

        ' "line one,⏎  line two"  →  "line one, line two"   (raw CR/LF inside
        ' a string value — collapse it to a space). Runs last so the earlier
        ' structural fixes have already balanced the quotes this string-token
        ' scan relies on.
        repaired = JsonStringTokenPattern.Replace(repaired, AddressOf CollapseRawNewlinesInString)

        Return repaired
    End Function

    ' Model wrote a string value across multiple physical lines, leaving a
    ' raw CR/LF between the quotes:
    '   `"OriginalText":"...chemotherapy,⏎     including thalidomide..."`
    '                                                                   (NCT00048230)
    ' JSON forbids unescaped control characters inside strings, so the parse
    ' fails. Collapse every newline run (with its surrounding indentation)
    ' down to a single space — the schema's text fields are logically
    ' single-line, so nothing is lost. A token with no raw newline is
    ' returned untouched, so well-formed string values round-trip unchanged
    ' and only genuinely-broken input is rewritten.
    ' Rebuilds a `"<Key>":"<value>"` slice with every unescaped interior `"`
    ' in the value turned into `\"`. Returns the match unchanged when the value
    ' has no bare quote, so the pass leaves already-valid fields byte-for-byte
    ' intact. Group 1 is the schema key, group 2 the raw value content.
    Private Shared Function EscapeInteriorQuotesInValue(m As Match) As String
        Dim content = m.Groups(2).Value
        If Not UnescapedQuoteInContentPattern.IsMatch(content) Then
            Return m.Value
        End If
        Dim escaped = UnescapedQuoteInContentPattern.Replace(content, "\" & ChrW(34))
        Return ChrW(34) & m.Groups(1).Value & ChrW(34) & ":" & ChrW(34) & escaped & ChrW(34)
    End Function

    Private Shared Function CollapseRawNewlinesInString(m As Match) As String
        Dim token = m.Value
        If token.IndexOf(ChrW(10)) < 0 AndAlso token.IndexOf(ChrW(13)) < 0 Then
            Return token
        End If
        Return RawNewlineRunPattern.Replace(token, " ")
    End Function

    ' Decodes one stringified-record token (`"{\"NCT_ID\":...}"`) into the raw
    ' object JSON it encodes. Returns the token unchanged unless it decodes to a
    ' JSON object carrying an NCT_ID, so ordinary string values that merely start
    ' with `{` are never rewritten. The unterminated final element lacks its
    ' closing wrapping `"`; a closing quote is appended so the token is a valid
    ' JSON string before decoding.
    Private Shared Function DecodeStringifiedRecordElement(m As Match) As String
        Dim token = m.Value
        Dim jsonStringToken = If(token.EndsWith(""""), token, token & """")
        Try
            Dim decoded = JsonSerializer.Deserialize(Of String)(jsonStringToken)
            If decoded IsNot Nothing Then
                Using doc = JsonDocument.Parse(decoded)
                    Dim probe As JsonElement
                    If doc.RootElement.ValueKind = JsonValueKind.Object AndAlso
                       doc.RootElement.TryGetProperty("NCT_ID", probe) Then
                        Return decoded
                    End If
                End Using
            End If
        Catch ex As JsonException
        End Try
        Return token
    End Function

    ''' <summary>
    ''' Spec section 2.5 step 9: if a whole batch produced zero records, emit a
    ''' single all-empty placeholder so the downstream per-item topology survives.
    ''' Persistence drops the placeholder because its NctId is empty.
    ''' </summary>
    Public Shared Function ApplyEmptyBatchSafetyNet(
            batchRecords As IReadOnlyList(Of CriterionRecord)) As IReadOnlyList(Of CriterionRecord)
        If batchRecords Is Nothing OrElse batchRecords.Count = 0 Then
            Return New CriterionRecord() {CriterionRecord.Empty}
        End If
        Return batchRecords
    End Function

    ' --- step 1 ---
    Private Shared Function ExtractTextField(rawResponse As String) As String
        Try
            Using doc = JsonDocument.Parse(rawResponse)
                Select Case doc.RootElement.ValueKind
                    Case JsonValueKind.Object
                        For Each fieldName In TextFieldNames
                            Dim element As JsonElement = Nothing
                            If doc.RootElement.TryGetProperty(fieldName, element) AndAlso
                               element.ValueKind = JsonValueKind.String Then
                                Dim value = element.GetString()
                                If Not String.IsNullOrWhiteSpace(value) Then
                                    Return value
                                End If
                            End If
                        Next
                        Return ""
                    Case JsonValueKind.String
                        Return If(doc.RootElement.GetString(), "")
                    Case Else
                        ' Already an array or scalar — treat the original input as the payload.
                        Return rawResponse
                End Select
            End Using
        Catch ex As JsonException
            ' Not JSON at all: treat as raw text payload (the model returned bare text).
            Return rawResponse
        End Try
    End Function

    ' --- step 4 ---
    Private Shared Function FindFirstBracket(text As String) As Integer
        Dim openArray = text.IndexOf("["c)
        Dim openObject = text.IndexOf("{"c)
        If openArray < 0 Then Return openObject
        If openObject < 0 Then Return openArray
        Return Math.Min(openArray, openObject)
    End Function

    ' --- step 6 ---
    Private Shared Function EmitRecords(
            root As JsonElement,
            trialNctId As String) As IReadOnlyList(Of CriterionRecord)
        Dim records As New List(Of CriterionRecord)
        Select Case root.ValueKind
            Case JsonValueKind.Array
                For Each element In root.EnumerateArray()
                    Select Case element.ValueKind
                        Case JsonValueKind.Object
                            records.Add(BuildRecord(element, trialNctId))
                        Case JsonValueKind.String
                            ' Model occasionally emits a record as a stringified
                            ' (escaped) JSON object array element rather than a
                            ' real object (NCT06689254). When such an element
                            ' parses cleanly, recover it here; plain (non-record)
                            ' string elements are ignored.
                            Dim recovered = TryBuildRecordFromStringElement(element.GetString(), trialNctId)
                            If recovered IsNot Nothing Then records.Add(recovered)
                    End Select
                Next
            Case JsonValueKind.Object
                records.Add(BuildRecord(root, trialNctId))
        End Select
        Return records
    End Function

    ' Recovers a record from an array element that is a stringified JSON object
    ' (`"{\"NCT_ID\":...}"`). Returns Nothing unless the decoded content is a
    ' JSON object carrying an NCT_ID, so plain string elements never become
    ' spurious records.
    Private Shared Function TryBuildRecordFromStringElement(value As String, trialNctId As String) As CriterionRecord
        If String.IsNullOrWhiteSpace(value) Then Return Nothing
        Dim trimmed = value.TrimStart()
        If trimmed.Length = 0 OrElse trimmed(0) <> "{"c Then Return Nothing
        Try
            Using doc = JsonDocument.Parse(value)
                Dim probe As JsonElement
                If doc.RootElement.ValueKind = JsonValueKind.Object AndAlso
                   doc.RootElement.TryGetProperty("NCT_ID", probe) Then
                    Return BuildRecord(doc.RootElement, trialNctId)
                End If
            End Using
        Catch ex As JsonException
        End Try
        Return Nothing
    End Function

    Private Shared Function BuildRecord(element As JsonElement, trialNctId As String) As CriterionRecord
        ' NCT_ID is authoritative from batch selection — it's the trial we sent
        ' to the LLM. The model is instructed to echo it on every record, but it
        ' can typo or transpose a digit (observed in production: NCT07386838
        ' emitted for NCT07386938), which would silently persist that row under
        ' the wrong trial and orphan it from the per-trial DELETE+INSERT. So the
        ' batch's trialNctId always wins; the model's echoed value is used only
        ' as a fallback when the caller supplies no trial id (defensive — the
        ' orchestrator always does).
        Dim recordNctId As String
        If Not String.IsNullOrWhiteSpace(trialNctId) Then
            recordNctId = trialNctId
        Else
            recordNctId = GetStringOrEmpty(element, "NCT_ID")
        End If
        Return New CriterionRecord(
                nctId:=recordNctId,
                criterion:=GetStringOrEmpty(element, "Criterion"),
                domain:=GetStringOrEmpty(element, "Domain"),
                concept:=GetStringOrEmpty(element, "Concept"),
                qualifier:=GetStringOrEmpty(element, "Qualifier"),
                timeWindow:=GetStringOrEmpty(element, "TimeWindow"),
                originalText:=StripBullet(GetStringOrEmpty(element, "OriginalText")))
    End Function

    Private Shared Function GetStringOrEmpty(element As JsonElement, propertyName As String) As String
        Dim child As JsonElement = Nothing
        If Not element.TryGetProperty(propertyName, child) Then Return ""
        Select Case child.ValueKind
            Case JsonValueKind.String
                Return If(child.GetString(), "")
            Case JsonValueKind.Number
                Return child.GetRawText()
            Case JsonValueKind.True
                Return "true"
            Case JsonValueKind.False
                Return "false"
            Case Else
                Return ""
        End Select
    End Function

    ' --- step 7 ---
    ' Spec regex is `^\s*[*\-•·◦]\s*` — anchored at start, no trailing trim.
    Private Shared Function StripBullet(originalText As String) As String
        If String.IsNullOrEmpty(originalText) Then Return ""
        Return BulletPattern.Replace(originalText, "")
    End Function

End Class
