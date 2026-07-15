Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports EligibilityProcessing.Core
Imports Xunit

' One test class per spec section 2.5 step. Each `Step<N>_...` test maps to a
' numbered rule so a future spec change can be cross-referenced against the
' test suite by grep.

Public Class LlmResponseParserTests

    Private ReadOnly _parser As New LlmResponseParser()
    Private Const TrialId As String = "NCT00000001"

    ' ============ step 1: extract text from one of {text, output, message, content} ============

    <Theory>
    <InlineData("text")>
    <InlineData("output")>
    <InlineData("message")>
    <InlineData("content")>
    Public Sub Step1_extracts_text_payload_from_any_supported_envelope_field(fieldName As String)
        Dim envelope = WrapEnvelope(fieldName, CriterionArray(Crit(concept:="Adult")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Adult", record.Concept)
    End Sub

    <Fact>
    Public Sub Step1_picks_first_populated_field_in_priority_order()
        Dim doc = New Dictionary(Of String, String) From {
            {"text", CriterionArray(Crit(concept:="FromText"))},
            {"output", CriterionArray(Crit(concept:="FromOutput"))}
        }
        Dim envelope = JsonSerializer.Serialize(doc)
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("FromText", record.Concept)
    End Sub

    <Fact>
    Public Sub Step1_falls_through_when_earlier_field_is_empty_string()
        Dim doc = New Dictionary(Of String, String) From {
            {"text", ""},
            {"content", CriterionArray(Crit(concept:="FromContent"))}
        }
        Dim envelope = JsonSerializer.Serialize(doc)
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("FromContent", record.Concept)
    End Sub

    <Fact>
    Public Sub Step1_treats_bare_text_as_payload_when_envelope_is_not_json()
        ' Some models emit bare JSON arrays without an envelope wrapper.
        Dim bareArray = CriterionArray(Crit(concept:="Adult"))
        Dim result = _parser.Parse(bareArray, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Adult", record.Concept)
    End Sub

    ' ============ step 2: skip null / empty / whitespace responses ============

    <Fact>
    Public Sub Step2_returns_empty_for_null_response()
        Dim result = _parser.Parse(Nothing, TrialId)
        Assert.Empty(result)
    End Sub

    <Theory>
    <InlineData("")>
    <InlineData(" ")>
    <InlineData("   ")>
    <InlineData(vbCrLf)>
    Public Sub Step2_returns_empty_for_empty_or_whitespace_response(rawResponse As String)
        Dim result = _parser.Parse(rawResponse, TrialId)
        Assert.Empty(result)
    End Sub

    ' ============ step 3: strip code fences ============

    <Fact>
    Public Sub Step3_strips_opening_json_fence()
        Dim payload = "```json" & vbLf & CriterionArray(Crit(concept:="Adult")) & vbLf & "```"
        Dim envelope = WrapEnvelope("text", payload)
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Adult", record.Concept)
    End Sub

    <Fact>
    Public Sub Step3_strips_bare_opening_fence()
        Dim payload = "```" & vbLf & CriterionArray(Crit(concept:="Adult")) & vbLf & "```"
        Dim envelope = WrapEnvelope("text", payload)
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Adult", record.Concept)
    End Sub

    <Fact>
    Public Sub Step3_is_noop_when_no_fence_present()
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(concept:="Adult")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Adult", record.Concept)
    End Sub

    ' ============ step 4: discard preamble before first [ or { ============

    <Fact>
    Public Sub Step4_discards_preamble_before_array_bracket()
        Dim payload = "Here is the JSON: " & CriterionArray(Crit(concept:="Adult"))
        Dim envelope = WrapEnvelope("text", payload)
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Adult", record.Concept)
    End Sub

    <Fact>
    Public Sub Step4_discards_preamble_before_object_brace()
        Dim payload = "Note: " & JsonSerializer.Serialize(Crit(concept:="Adult"))
        Dim envelope = WrapEnvelope("text", payload)
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Adult", record.Concept)
    End Sub

    <Fact>
    Public Sub Step4_returns_empty_when_no_bracket_present()
        Dim envelope = WrapEnvelope("text", "no json at all here, only prose")
        Dim result = _parser.Parse(envelope, TrialId)
        Assert.Empty(result)
    End Sub

    ' ============ step 5: silently drop on parse failure ============

    <Fact>
    Public Sub Step5_drops_silently_on_malformed_json()
        Dim envelope = WrapEnvelope("text", "[{""missing"": ""close brace""")
        Dim result = _parser.Parse(envelope, TrialId)
        Assert.Empty(result)
    End Sub

    <Fact>
    Public Sub Step5_drops_silently_on_truncated_array()
        Dim envelope = WrapEnvelope("text", "[{""Concept"": ""Adult""},")
        Dim result = _parser.Parse(envelope, TrialId)
        Assert.Empty(result)
    End Sub

    ' ============ step 6: array fan-out vs single-object ============

    <Fact>
    Public Sub Step6_fans_out_array_to_one_record_per_element()
        Dim envelope = WrapEnvelope("text", CriterionArray(
            Crit(concept:="A"), Crit(concept:="B"), Crit(concept:="C")))
        Dim result = _parser.Parse(envelope, TrialId)
        Assert.Equal(3, result.Count)
        Assert.Equal(New String() {"A", "B", "C"}, result.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Step6_emits_single_record_for_object_payload()
        Dim envelope = WrapEnvelope("text", JsonSerializer.Serialize(Crit(concept:="Solo")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Solo", record.Concept)
    End Sub

    <Fact>
    Public Sub Step6_returns_empty_for_empty_array()
        Dim envelope = WrapEnvelope("text", "[]")
        Dim result = _parser.Parse(envelope, TrialId)
        Assert.Empty(result)
    End Sub

    ' ============ step 7: bullet-marker stripping (regex anchored at start) ============

    <Theory>
    <InlineData("*")>
    <InlineData("-")>
    <InlineData("•")>
    <InlineData("·")>
    <InlineData("◦")>
    Public Sub Step7_strips_each_supported_bullet_marker(marker As String)
        Dim envelope = WrapEnvelope("text", CriterionArray(
            Crit(originalText:=marker & " Patient is an adult")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Patient is an adult", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Step7_strips_whitespace_around_marker()
        Dim envelope = WrapEnvelope("text", CriterionArray(
            Crit(originalText:="   *   Patient is an adult")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Patient is an adult", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Step7_preserves_trailing_whitespace_per_anchored_regex()
        ' Spec regex /^\s*[*\-•·◦]\s*/ is start-anchored; trailing whitespace stays.
        Dim envelope = WrapEnvelope("text", CriterionArray(
            Crit(originalText:="* Patient   ")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Patient   ", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Step7_is_noop_when_no_marker_present()
        Dim envelope = WrapEnvelope("text", CriterionArray(
            Crit(originalText:="Patient is an adult")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal("Patient is an adult", record.OriginalText)
    End Sub

    ' ============ step 8: pairing (NCT_ID linkage to source trial) ============

    <Fact>
    Public Sub Step8_uses_parameter_nctId_when_llm_omits_it()
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(nctId:="", concept:="Adult")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal(TrialId, record.NctId)
    End Sub

    <Fact>
    Public Sub Step8_overrides_llm_nctId_with_authoritative_trial_id()
        ' The NCT_ID we sent in the batch is authoritative. If the model echoes
        ' back a different id (a typo / transposed digit, observed in production),
        ' the trial-id parameter wins so the record can't be persisted under the
        ' wrong trial. The model's value is not trusted when a trial id is known.
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(nctId:="NCT99999999", concept:="Adult")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        Assert.Equal(TrialId, record.NctId)
    End Sub

    <Fact>
    Public Sub Step8_stamps_each_record_in_array_with_nctId()
        Dim envelope = WrapEnvelope("text", CriterionArray(
            Crit(nctId:="", concept:="A"),
            Crit(nctId:="", concept:="B")))
        Dim result = _parser.Parse(envelope, TrialId)
        Assert.All(result, Sub(r) Assert.Equal(TrialId, r.NctId))
    End Sub

    <Fact>
    Public Sub Step8_overrides_a_single_typoed_nctId_among_correct_records()
        ' The production shape: the model echoes the correct id on every record
        ' but transposes a digit on one (here record B). All records must be
        ' stamped with the authoritative trial id so the stray one isn't
        ' persisted under a different trial.
        Dim envelope = WrapEnvelope("text", CriterionArray(
            Crit(nctId:=TrialId, concept:="A"),
            Crit(nctId:="NCT00000002", concept:="B"),
            Crit(nctId:=TrialId, concept:="C")))
        Dim result = _parser.Parse(envelope, TrialId)
        Assert.Equal(3, result.Count)
        Assert.All(result, Sub(r) Assert.Equal(TrialId, r.NctId))
    End Sub

    ' ============ step 9: empty-batch safety-net placeholder ============

    <Fact>
    Public Sub Step9_emits_placeholder_when_batch_produces_zero_records()
        Dim result = LlmResponseParser.ApplyEmptyBatchSafetyNet(Array.Empty(Of CriterionRecord)())
        Dim record = Assert.Single(result)
        Assert.Equal("", record.NctId)
        Assert.Equal("", record.Concept)
        Assert.Equal("", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Step9_handles_null_batch_as_empty()
        Dim result = LlmResponseParser.ApplyEmptyBatchSafetyNet(Nothing)
        Dim record = Assert.Single(result)
        Assert.Equal("", record.NctId)
    End Sub

    <Fact>
    Public Sub Step9_passes_through_non_empty_batch_unchanged()
        Dim records As IReadOnlyList(Of CriterionRecord) = New CriterionRecord() {
            New CriterionRecord("NCT1", "Inclusion", "Age", "Adult", "", "", "Age >= 18")
        }
        Dim result = LlmResponseParser.ApplyEmptyBatchSafetyNet(records)
        Assert.Same(records, result)
    End Sub

    ' ============ end-to-end shape ============

    <Fact>
    Public Sub Full_record_round_trip_preserves_every_field()
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(
            nctId:="NCT00000123",
            criterion:="Exclusion",
            domain:="Disease",
            concept:="Diabetes",
            qualifier:="Type II",
            timeWindow:="within 12 months",
            originalText:="* History of Type II diabetes within 12 months")))
        Dim result = _parser.Parse(envelope, TrialId)
        Dim record = Assert.Single(result)
        ' NctId is stamped from the authoritative trial id, not round-tripped
        ' from the model's echoed "NCT00000123" — see Step8 override test.
        Assert.Equal(TrialId, record.NctId)
        Assert.Equal("Exclusion", record.Criterion)
        Assert.Equal("Disease", record.Domain)
        Assert.Equal("Diabetes", record.Concept)
        Assert.Equal("Type II", record.Qualifier)
        Assert.Equal("within 12 months", record.TimeWindow)
        Assert.Equal("History of Type II diabetes within 12 months", record.OriginalText)
    End Sub

    ' ============ ParseWithOutcome (distinguishes empty_array vs invalid_json) ============

    <Fact>
    Public Sub ParseWithOutcome_records_success_when_records_emitted()
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(concept:="Adult")))
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)
        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.Single(result.Records)
    End Sub

    <Fact>
    Public Sub ParseWithOutcome_records_empty_array_when_LLM_returns_empty_brackets()
        Dim envelope = WrapEnvelope("text", "[]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)
        Assert.Equal(LlmParseResult.OutcomeEmptyArray, result.Outcome)
        Assert.Empty(result.Records)
    End Sub

    <Fact>
    Public Sub ParseWithOutcome_records_invalid_json_when_LLM_output_is_truncated()
        ' Simulate a max_tokens truncation — opening [ but no closing brace.
        Dim truncated = "[{""NCT_ID"":""NCT00000001"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""Coronary Artery"
        Dim envelope = WrapEnvelope("text", truncated)
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)
        Assert.Equal(LlmParseResult.OutcomeInvalidJson, result.Outcome)
        Assert.Empty(result.Records)
    End Sub

    <Fact>
    Public Sub ParseWithOutcome_records_invalid_json_when_payload_has_no_brackets()
        Dim envelope = WrapEnvelope("text", "I'm sorry, I cannot extract criteria from this trial.")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)
        Assert.Equal(LlmParseResult.OutcomeInvalidJson, result.Outcome)
        Assert.Empty(result.Records)
    End Sub

    <Fact>
    Public Sub ParseWithOutcome_records_invalid_json_for_empty_payload()
        Dim result = _parser.ParseWithOutcome("", TrialId)
        Assert.Equal(LlmParseResult.OutcomeInvalidJson, result.Outcome)
        Assert.Empty(result.Records)
    End Sub

    ' ============ JSON-repair pass (rescues common LLM mistakes) ============

    <Fact>
    Public Sub Repair_inserts_missing_colon_after_key_when_value_starts_with_gte()
        ' Reproduces the production bug we hit on NCT00000105: model wrote
        ' `"Qualifier">= 70%"` — dropped the colon AND the opening quote of
        ' the value, but kept the closing quote.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Performance Status"",""Concept"":""Karnofsky"",""Qualifier"">= 70%"",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal(">= 70%", record.Qualifier)
    End Sub

    <Fact>
    Public Sub Repair_inserts_missing_colon_when_value_starts_with_parenthesis()
        ' Reproduces NCT00000717 production failure: model wrote
        ' `"Concept"(A-a) DO2"` — dropped the colon AND the opening quote
        ' of the value, but kept the closing quote. The value happens to
        ' start with `(`, which the original lookahead `[<>=!+]` didn't
        ' cover. Broadened lookahead now handles any non-JSON-continuation
        ' character (parens, brackets, digits, letters — anything that
        ' isn't `:` `,` `]` or `}`).
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Cardiovascular Function"",""Concept""(A-a) DO2"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("(A-a) DO2", record.Concept)
    End Sub

    <Fact>
    Public Sub Repair_strips_trailing_comma_before_closing_bracket()
        ' Some models emit JSON5-flavoured trailing commas.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""Diabetes"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""x"",}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
    End Sub

    <Fact>
    Public Sub Repair_was_not_applied_when_input_already_valid()
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(concept:="Adult")))
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
    End Sub

    <Fact>
    Public Sub Repair_falls_through_to_invalid_json_when_unrepairable()
        ' Missing closing brace + bracket — repair patterns don't cover this.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion""")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeInvalidJson, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Empty(result.Records)
    End Sub

    <Fact>
    Public Sub Repair_collapses_extra_closing_quote_after_value()
        ' Reproduces NCT00000284 production failure: model emitted an extra
        ' `"` after a properly-closed string value — `"pregnant or lactating""}`
        ' instead of `"pregnant or lactating"}`.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Pregnancy"",""Concept"":""Pregnancy"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""pregnant or lactating""""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("pregnant or lactating", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_collapse_legitimate_empty_string_values()
        ' Safety check: `""` after `:` is an intentional empty string and
        ' must survive the repair pass untouched. Only the
        ' content-followed-by-`""}` form gets collapsed.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""Diabetes"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("", record.Qualifier)
        Assert.Equal("", record.TimeWindow)
    End Sub

    <Fact>
    Public Sub Repair_inserts_empty_value_for_orphan_schema_key()
        ' Reproduces NCT00053560: model dropped the `:` AND the value for the
        ' Concept key, leaving an orphan `"Concept",` sandwiched between
        ' well-formed key:value pairs. Repair inserts an empty-string value
        ' so the rest of the record survives. The lookbehind on [{,] keeps
        ' this from firing on the same key inside an OriginalText string.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Disease"",""Concept"",""Qualifier"":""no evidence"",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("", record.Concept)
        Assert.Equal("no evidence", record.Qualifier)
    End Sub

    <Fact>
    Public Sub Repair_strips_bare_escape_between_array_records()
        ' Reproduces NCT00053495: tokenizer leaked a literal `\n` between the
        ' previous record's closing `},` and the next `{`. JSON parses `\n`
        ' only inside string values; outside, even a valid-looking escape is
        ' a syntax error. Strip the orphan escape so the two records survive.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""A"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""},\n{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""B"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(2, result.Records.Count)
        Assert.Equal("A", result.Records(0).Concept)
        Assert.Equal("B", result.Records(1).Concept)
    End Sub

    <Fact>
    Public Sub Repair_strips_bare_escape_when_real_newline_and_indent_sit_between_comma_and_escape()
        ' Reproduces NCT00011375: same `,\n{` shape as NCT00053495's defect
        ' but the response is pretty-printed by the model — a real newline
        ' and indentation sit between the array separator `,` and the
        ' literal `\n`. The `\s*` in the regex must consume both before
        ' the `\\\w+` literal-escape match.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""A"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""}," & vbLf & "  \n{""NCT_ID"":""NCT1"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""B"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(2, result.Records.Count)
    End Sub

    <Fact>
    Public Sub Repair_strips_orphan_quoted_string_between_brace_and_NCT_ID_key()
        ' Reproduces NCT00053495 second defect: the model emitted an orphan
        ' quoted string (here `"n{"`) between the opening `{` and the real
        ' first key `"NCT_ID":`. Strip it.
        Dim broken = WrapEnvelope("text",
                "[{""n{""""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""y""}]")
        Dim result = _parser.ParseWithOutcome(broken, "NCT0")

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("NCT0", record.NctId)
        Assert.Equal("x", record.Concept)
    End Sub

    <Fact>
    Public Sub Repair_strips_stray_record_separator_fragment_before_NCT_ID_key()
        ' Reproduces NCT00048230: the model fused a stray `"},{` record-
        ' separator fragment onto the front of an object, before its first
        ' real key — `{"},{"NCT_ID":...` instead of `{"NCT_ID":...`. The
        ' fragment must be stripped without dropping the record.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT00048230"",""Criterion"":""Exclusion"",""Domain"":""Drug Treatment"",""Concept"":""Dexamethasone"",""Qualifier"":""refractory"",""TimeWindow"":"""",""OriginalText"":""a""},{""},{""NCT_ID"":""NCT00048230"",""Criterion"":""Exclusion"",""Domain"":""Surgery"",""Concept"":""Major surgery"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(2, result.Records.Count)
        Assert.Equal("Dexamethasone", result.Records(0).Concept)
        Assert.Equal("Major surgery", result.Records(1).Concept)
    End Sub

    <Fact>
    Public Sub Repair_collapses_raw_newline_inside_a_string_value()
        ' Reproduces NCT00048230's second defect: the model wrote an
        ' OriginalText value across two physical lines, leaving a raw newline
        ' (plus indentation) inside the quotes. JSON forbids unescaped control
        ' chars in strings, so the parse fails; repair collapses the break to
        ' a single space.
        Dim payload = "[{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Drug Treatment"",""Concept"":""Chemotherapy"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""Patient received nitrosoureas or any other chemotherapy," & vbLf & "     including thalidomide""}]"
        Dim broken = WrapEnvelope("text", payload)
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("Patient received nitrosoureas or any other chemotherapy, including thalidomide", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_alter_string_values_without_raw_newlines()
        ' A well-formed response that fails the first parse for an unrelated
        ' reason (trailing comma) must round-trip its string values through
        ' the newline-collapse pass byte-for-byte — multi-space runs and all.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Age"",""Concept"":""Adult"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""Age  >=  18 years""},]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("Age  >=  18 years", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_moves_stray_closing_paren_back_inside_value_quotes()
        ' Reproduces NCT00007839: model closed the value's `"` before
        ' closing the parenthetical, leaving `"...fistula")` instead of
        ' `"...fistula)"`. The orphan `)` between the close-quote and the
        ' next structural char breaks the parse. Repair moves the paren
        ' inside so the parenthetical survives.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Infection"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""No active bacterial infections (e.g., abscess or with fistula"")}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("No active bacterial infections (e.g., abscess or with fistula)", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_handles_multiple_stray_parens_after_value()
        ' Two opening parens, both closed outside the string: greedy `\)+`
        ' should pull both inside.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""nested ((case""))}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("nested ((case))", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_touch_legitimate_paren_inside_value()
        ' `(e.g., x)` properly enclosed by quotes — the close-paren sits
        ' INSIDE the string, not orphaned after the closing quote. Pattern
        ' must not fire.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""infection (e.g., abscess)""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal("infection (e.g., abscess)", result.Records.Single().OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_truncated_response_strips_partial_trailing_record_and_closes_array()
        ' Reproduces NCT00030147: the response was cut off after the last
        ' well-formed record's `}`, with an orphan `{` and no closing `]`.
        ' The repair strips the partial start and adds the missing `]` so
        ' the records that DID complete still parse and persist.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""y""}{")
        Dim result = _parser.ParseWithOutcome(broken, "NCT0")

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("NCT0", record.NctId)
        Assert.Equal("y", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_truncated_response_handles_partial_fields_inside_trailing_record()
        ' Truncation can happen mid-field too — the partial `{...` carries
        ' some content but the closing `}` and `]` are missing. Same fix:
        ' strip the partial and close the array.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""y""}{""NCT_ID"":""NCT1"",""Criterion"":""Excl")
        Dim result = _parser.ParseWithOutcome(broken, "NCT0")

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("NCT0", record.NctId)
    End Sub

    <Fact>
    Public Sub Repair_truncation_pattern_does_not_fire_on_well_formed_response()
        ' A complete `[{...}]` ends with `}]`, not `}{`. The truncation
        ' regex's `\s*\{` requires a `{` immediately following the trailing
        ' `}` — the closing `]` is the safety stop.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""y""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
    End Sub

    <Fact>
    Public Sub Repair_strips_n_prefix_glued_onto_schema_key_name()
        ' Reproduces NCT00053495 (re-run): same tokenizer-leak family as the
        ' orphan-string defect, but here the `n` got fused directly into the
        ' NCT_ID key with no separator — `"nNCT_ID":"NCT00053495"`. Strip
        ' the prefix so the record parses with the schema-canonical key.
        '
        ' `nNCT_ID` is technically valid JSON (just an unrecognised key) so
        ' on its own it would parse on the first attempt and the repair
        ' pass would never run — NctId would silently fall back to the
        ' trial-id parameter. Adding a trailing comma forces parse failure
        ' so the repair pass runs; both fixes apply in the same pass.
        Dim broken = WrapEnvelope("text",
                "[{""nNCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""y"",}]")
        Dim result = _parser.ParseWithOutcome(broken, "NCT0")

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("NCT0", record.NctId)
    End Sub

    <Fact>
    Public Sub Repair_does_not_strip_real_NCT_ID_key_as_orphan()
        ' Safety: a well-formed object whose first key IS NCT_ID must not
        ' have its NCT_ID stripped as an orphan — the lookahead's backtracking
        ' must back off to leave the real key intact.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""y""}]")
        Dim result = _parser.ParseWithOutcome(envelope, "NCT0")

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("NCT0", record.NctId)
    End Sub

    <Fact>
    Public Sub Repair_does_not_fire_on_schema_key_name_inside_a_value_string()
        ' Safety: an OriginalText that mentions one of the schema keys by
        ' name (e.g. "TimeWindow") must not trip the orphan-key repair —
        ' the lookbehind requires the match to start at an object-key
        ' position ([{,]), not inside a string value where the preceding
        ' char is the value's opening `"`.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""Concept is documented""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal("Concept is documented", result.Records.Single().OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_strips_unquoted_commentary_and_alternative_value_after_real_value()
        ' Reproduces NCT00004907 production failure: model appended unquoted
        ' commentary and an alternative quoted string between the real value
        ' and the closing brace:
        '   "OriginalText":"x* OR y*" restated: "x"}
        ' Repair must keep the value the model actually emitted (the first
        ' string) and drop everything from the commentary through the
        ' alternative string up to `}`.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Laboratory Test"",""Concept"":""DLCO"",""Qualifier"":""at least 50% of predicted"",""TimeWindow"":"""",""OriginalText"":""DLCO at least 50% of predicted* OR FEV1 and/or FVC at least 50% of predicted*"" restated: ""DLCO at least 50% of predicted""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("DLCO at least 50% of predicted* OR FEV1 and/or FVC at least 50% of predicted*", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_collapse_adjacent_key_value_pairs()
        ' Safety check: a properly-formed object with multiple key:value pairs
        ' separated by commas must not match the trailing-commentary pattern.
        ' The pattern requires at least one whitespace + non-quote garbage
        ' BEFORE the next quoted string, so `"a":"b","c":"d"` (comma between
        ' pairs) cannot match.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""Diabetes"",""Qualifier"":""controlled"",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("controlled", record.Qualifier)
        Assert.Equal("x", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_inserts_missing_NCT_ID_key_when_bare_id_emitted_as_value()
        ' Reproduces NCT00000439 production failure: model dropped the
        ' `"NCT_ID":` key on one record in an array, leaving the bare NCT ID
        ' where a key:value pair belonged — `{"NCT00000439","Criterion":...}`
        ' instead of `{"NCT_ID":"NCT00000439","Criterion":...}`.
        Dim broken = WrapEnvelope("text",
                "[{""NCT00000439"",""Criterion"":""Exclusion"",""Domain"":""Substance Use"",""Concept"":""Intravenous Drug Use"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(broken, "NCT00000439")

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("NCT00000439", record.NctId)
        Assert.Equal("Intravenous Drug Use", record.Concept)
    End Sub

    <Fact>
    Public Sub Repair_inserts_missing_NCT_ID_key_only_on_the_affected_record_in_a_mixed_array()
        ' Closer to the production shape: most records are well-formed but
        ' one (here the second) is missing the NCT_ID key. The repair must
        ' fix the broken one without touching the good ones.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT00000439"",""Criterion"":""Inclusion"",""Domain"":""Substance Use"",""Concept"":""Alcohol Dependence"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""}," &
                "{""NCT00000439"",""Criterion"":""Exclusion"",""Domain"":""Substance Use"",""Concept"":""Intravenous Drug Use"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}]")
        Dim result = _parser.ParseWithOutcome(broken, "NCT00000439")

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(2, result.Records.Count)
        Assert.All(result.Records, Sub(r) Assert.Equal("NCT00000439", r.NctId))
        Assert.Equal(New String() {"Alcohol Dependence", "Intravenous Drug Use"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Repair_does_not_fire_when_NCT_ID_key_is_present()
        ' Safety check: a well-formed `"NCT_ID":"NCT00000439"` must not trip
        ' the repair pass. The pattern's lookahead requires `,` after the
        ' bare ID, so the legitimate `:` between key and value should keep
        ' the regex from matching at all.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT00000439"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""Diabetes"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(envelope, "NCT00000439")

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal("NCT00000439", result.Records.Single().NctId)
    End Sub

    <Fact>
    Public Sub Repair_inserts_missing_key_closing_quote_when_key_uses_equals_instead_of_colon()
        ' Reproduces NCT00003906 production failure: model dropped BOTH the
        ' key's closing quote and used `=` instead of `:` —
        '   "Qualifier="no clinical evidence"
        ' should be
        '   "Qualifier":"no clinical evidence"
        ' (Distinct from EqualsForColon, which assumes both key quotes are
        ' present; that pattern can't fire when the closing quote is missing.)
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Disease"",""Concept"":""Malignancy"",""Qualifier=""no clinical evidence"",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("no clinical evidence", record.Qualifier)
    End Sub

    <Fact>
    Public Sub Repair_does_not_fire_on_value_content_containing_equals()
        ' Safety: a value like "FEV1 = at least 50%" is well-formed JSON. The
        ' MalformedKeyEquals pattern's lookbehind `(?<=[{,])` must prevent
        ' the regex from matching inside string values.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Laboratory Test"",""Concept"":""FEV1"",""Qualifier"":""FEV1 = at least 50%"",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("FEV1 = at least 50%", record.Qualifier)
    End Sub

    <Fact>
    Public Sub Repair_strips_garbage_in_key_and_inserts_colon_plus_value_quote()
        ' Reproduces NCT00002378 production failure: model emitted garbage
        ' inside the key's quotes AND a `>` separator AND a value with no
        ' opening quote, all at once:
        '   "OriginalText કરતા "> 3 months..."}
        ' Repair drops the garbage, swaps `>` for `:`, and re-inserts the
        ' value's missing opening quote.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Drug Treatment"",""Concept"":""Antiretroviral therapy"",""Qualifier"":""cumulative"",""TimeWindow"":""more than 3 months"",""OriginalText કરતા ""> 3 months cumulative therapy with antiretrovirals.""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("3 months cumulative therapy with antiretrovirals.", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_fire_on_legit_value_containing_gt()
        ' Safety: a value that legitimately contains `>` between quoted
        ' parts (still inside ONE JSON string) must not trip the garbage-key
        ' pattern. The lookbehind `(?<=[{,])` is what saves this — the inner
        ' `>` is preceded by `:` (key terminator), not `{` or `,`.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""X"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""systolic > 140 mmHg""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("systolic > 140 mmHg", record.OriginalText)
    End Sub

    <Theory>
    <InlineData("<")>
    <InlineData(">")>
    <InlineData("~")>
    Public Sub Repair_replaces_operator_separator_with_colon_between_quoted_key_and_quoted_value(sep As String)
        ' Generalises the original `=` case to other operator-like wrong
        ' separators the model has emitted. NCT00001043 hit the `<` variant:
        '   "OriginalText"<"12 months of age"}
        ' Other variants (`>`, `~`) covered defensively — same one-character
        ' shape, same fix.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""Diabetes"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText""" & sep & """value""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("value", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_replaces_equals_with_colon_between_quoted_key_and_quoted_value()
        ' Reproduces NCT00000718 production failure: model wrote
        ' `"Qualifier"="elevated"` — used `=` instead of `:` between key
        ' and (properly quoted) value.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Vital Signs"",""Concept"":""Temperature"",""Qualifier""=""elevated"",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("elevated", record.Qualifier)
    End Sub

    <Fact>
    Public Sub Repair_strips_invalid_json_backslash_escapes_inside_string_values()
        ' Reproduces NCT00000317 production failure: model copied AACT's
        ' markdown-escape backslash (\>) verbatim into the JSON string,
        ' producing an invalid JSON escape sequence that System.Text.Json
        ' rejects.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Cardiovascular Function"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""QRS duration \>/= 0.11""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("QRS duration >/= 0.11", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_preserves_properly_escaped_backslashes()
        ' \\X is valid JSON (escaped backslash + literal char). The negative
        ' lookbehind in InvalidJsonEscapePattern must not turn this into \X.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""path is C:\\Users\\test""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal("path is C:\Users\test", result.Records.Single().OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_touch_valid_value_that_happens_to_contain_gt_inside_quotes()
        ' Sanity: a value with `>=` properly quoted should parse on the first
        ' attempt without the repair pass firing.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Performance Status"",""Concept"":""Karnofsky"",""Qualifier"":"">= 70%"",""TimeWindow"":"""",""OriginalText"":""x""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal(">= 70%", result.Records.Single().Qualifier)
    End Sub

    <Fact>
    Public Sub Repair_escapes_unescaped_interior_quotes_in_terminal_value()
        ' Reproduces NCT07605728 production failure: the model copied a quoted
        ' parenthetical into OriginalText without escaping the interior quotes -
        '   `("Synbiotic Supplement" or placebo)`
        ' so System.Text.Json ended the value at the first bare `"`. OriginalText
        ' is the last field, so the value's real close is followed by `}`.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Lifestyle"",""Concept"":""Probiotic Use"",""Qualifier"":""willingness to refrain"",""TimeWindow"":"""",""OriginalText"":""Willingness to refrain from using probiotics (""Synbiotic Supplement"" or placebo) through study completion.""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("Willingness to refrain from using probiotics (""Synbiotic Supplement"" or placebo) through study completion.", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_escapes_unescaped_interior_quotes_in_non_terminal_value()
        ' Same defect in a field that is NOT last: the value's real close is
        ' followed by `,"<NextSchemaKey>"` rather than `}`. Proves the structural
        ' lookahead's comma branch finds the genuine boundary. The interior term
        ' is multi-word (`"active treatment"`) - a single-word bare quote is
        ' claimed by MissingColonAfterKey instead (a documented gap).
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":""the ""active treatment"" arm"",""TimeWindow"":"""",""OriginalText"":""y""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("the ""active treatment"" arm", record.Qualifier)
        Assert.Equal("y", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_treat_interior_quoted_term_before_comma_as_field_boundary()
        ' Robustness: interior quoted terms followed by a comma (not a schema
        ' key) must not be mistaken for the value's end. `"received "drug A",
        ' "drug B", or placebo"` has commas right after interior quotes, but the
        ' lookahead requires a schema key after the comma, so the lazy capture
        ' extends to the genuine close and all interior quotes are escaped.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Drug Treatment"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""received ""drug A"", ""drug B"", or placebo""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("received ""drug A"", ""drug B"", or placebo", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_double_escape_already_escaped_interior_quotes()
        ' Safety: a value whose interior quotes are correctly escaped (`\"`) is
        ' valid JSON, parses on the first attempt, and the repair pass never
        ' runs - so the quotes survive untouched, not doubled.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""he said \""hi\"" today""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("he said ""hi"" today", record.OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_strips_stray_trailing_brace_after_complete_array()
        ' Reproduces NCT07299487 production failure: the array is complete and
        ' well-formed, but the model emitted a stray `}` after the closing `]`.
        ' System.Text.Json rejects any non-whitespace after the root value, so
        ' the otherwise-perfect response never parses until the brace is stripped.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Age"",""Concept"":""Adult"",""Qualifier"":"""",""TimeWindow"":""at least 18 years"",""OriginalText"":""Subject of legal age""}]" & vbLf & "}")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("Adult", record.Concept)
    End Sub

    <Fact>
    Public Sub Repair_strips_multiple_stray_trailing_braces_after_array()
        ' Defensive: more than one duplicate closer after the array `]` collapses
        ' in a single pass.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""x"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""y""}]}}")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal("x", result.Records.Single().Concept)
    End Sub

    <Fact>
    Public Sub Repair_does_not_fire_on_array_with_no_trailing_brace()
        ' Safety: a well-formed array ending in `}]` parses on the first attempt;
        ' the trailing-brace strip never runs and `]` is the last char.
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(concept:="Adult")))
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal("Adult", result.Records.Single().Concept)
    End Sub

    <Fact>
    Public Sub Repair_closes_unterminated_array_when_records_are_complete()
        ' Reproduces NCT07295392 production failure: every record is whole, but
        ' the model forgot the array's closing `]`, so the payload ends on the
        ' last record's `}`. System.Text.Json hits EOF inside the still-open
        ' array. Repair appends the missing `]`.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Age"",""Concept"":""Adult"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""}," & vbLf &
                "{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Disease"",""Concept"":""Sepsis"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}" & vbLf)
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(2, result.Records.Count)
        Assert.Equal(New String() {"Adult", "Sepsis"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Repair_does_not_close_array_when_last_record_is_incomplete()
        ' Boundary: an unterminated array whose final record is cut off
        ' mid-value (not on a `}`) must NOT be force-closed - we only fabricate
        ' the `]` when the last record is whole, never invent a record boundary.
        ' Stays invalid_json so the partial data is dropped rather than guessed.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Age"",""Concept"":""Adult"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""},{""NCT_ID"":""NCT0"",""Criterion"":""Inc")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeInvalidJson, result.Outcome)
        Assert.Empty(result.Records)
    End Sub

    <Fact>
    Public Sub Repair_does_not_fire_on_well_formed_array_ending_in_bracket()
        ' Safety: a complete array ends in `]`, parses on the first attempt, and
        ' the unterminated-array fix never runs.
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(concept:="Adult")))
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal("Adult", result.Records.Single().Concept)
    End Sub

    <Fact>
    Public Sub Repair_strips_stray_quotes_wrapping_record_opening_braces()
        ' Reproduces NCT07275749 production failure: the model wrapped two
        ' records in stray `"` immediately before their opening `{` -
        '   `},"{"NCT_ID":...},{"NCT_ID":...},"{"NCT_ID":...`
        ' The parser reads `"{"` as the string `{` and then chokes on the bare
        ' `NCT_ID`. Dropping each stray `"` restores ordinary array elements.
        Dim q = ChrW(34)
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Age"",""Concept"":""Adult"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""}," &
                q & "{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Other"",""Concept"":""B"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}," &
                "{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Other"",""Concept"":""C"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""c""}," &
                q & "{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Disease"",""Concept"":""D"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""d""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(4, result.Records.Count)
        Assert.Equal(New String() {"Adult", "B", "C", "D"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Repair_strips_stray_quote_before_pretty_printed_record()
        ' The wrapping `"` can sit before a pretty-printed brace too - the
        ' `\s*` between `{` and `"NCT_ID"` must absorb the newline + indent.
        Dim q = ChrW(34)
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Age"",""Concept"":""Adult"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""}," & vbLf &
                q & "{" & vbLf & "    ""NCT_ID"": ""NCT0""," & vbLf & "    ""Criterion"": ""Exclusion""," & vbLf & "    ""Domain"": ""Disease""," & vbLf & "    ""Concept"": ""B""," & vbLf & "    ""Qualifier"": """"," & vbLf & "    ""TimeWindow"": """"," & vbLf & "    ""OriginalText"": ""b""" & vbLf & "  }]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(2, result.Records.Count)
        Assert.Equal(New String() {"Adult", "B"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Repair_does_not_strip_quote_at_normal_compact_record_boundary()
        ' Safety: a normal compact array separates records with `},{"NCT_ID"` -
        ' the `{` is preceded by `,`, not `"`, so no stray quote exists and the
        ' value-closing `"` before `}` is never adjacent to a `{`. Parses clean.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Age"",""Concept"":""Adult"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""},{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Disease"",""Concept"":""B"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal(New String() {"Adult", "B"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Repair_handles_stray_record_quote_combined_with_raw_newlines_in_values()
        ' Reproduces NCT07257926 production failure: a stray `"` wraps a compact
        ' record's opening brace (the NCT07275749 defect) AND that record's
        ' OriginalText is split across physical lines with raw newlines (the
        ' NCT00048230 defect). No new pattern is needed - StrayQuoteBeforeRecord
        ' (early) and CollapseRawNewlinesInString (last) must compose: the stray
        ' quote is dropped first, then the multi-line value collapses to one
        ' line, and the whole batch parses.
        Dim q = ChrW(34)
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""Adult"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""}," & vbLf &
                q & "{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Laboratory Test"",""Concept"":""Serum creatinine"",""Qualifier"":"""",""TimeWindow"":""previous 6 months"",""OriginalText"":""lacked complete data (serum creatinine," & vbLf & "   platelet count," & vbLf & "   and prothrombin time) pertaining to the previous 6 months""}," &
                "{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Allergy"",""Concept"":""Sucralfate allergy"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""those who had a known allergy to sucralfate""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(3, result.Records.Count)
        Assert.Equal(New String() {"Adult", "Serum creatinine", "Sucralfate allergy"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
        Assert.Equal("lacked complete data (serum creatinine, platelet count, and prothrombin time) pertaining to the previous 6 months",
                     result.Records(1).OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_inserts_missing_comma_between_complete_records_mid_array()
        ' Reproduces NCT07088458 production failure: the model dropped the comma
        ' between two complete records, leaving the previous `}` butted against
        ' the next `{` - `..."}{"NCT_ID":...` - in the MIDDLE of the array
        ' (not at end-of-input, so TruncatedTrailingRecord does not apply).
        ' Happens twice; both boundaries are repaired.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""A"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""}{" & vbLf &
                "  ""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""B"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}{" & vbLf &
                "  ""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Disease"",""Concept"":""C"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""c""}]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(3, result.Records.Count)
        Assert.Equal(New String() {"A", "B", "C"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Repair_does_not_fire_on_correctly_comma_separated_records()
        ' Safety: a normal `},{"NCT_ID"` boundary has a comma between the braces,
        ' which `\}\s*\{` cannot match (`\s*` consumes whitespace, not `,`), so
        ' the missing-comma fix never touches well-formed input.
        Dim envelope = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""A"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""},{""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Disease"",""Concept"":""B"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""b""}]")
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal(New String() {"A", "B"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Repair_inserts_missing_brace_when_last_record_closed_by_array_bracket()
        ' Reproduces NCT06692166 production failure: the model closed the last
        ' record with the array `]` instead of the object `}` - the final object
        ' never gets its brace, so the parser hits `]` while still inside it.
        ' The previous records are well-formed; only the last object's `}` is
        ' missing before the array close.
        Dim broken = WrapEnvelope("text",
                "[{""NCT_ID"":""NCT0"",""Criterion"":""Inclusion"",""Domain"":""Disease"",""Concept"":""A"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""a""}," & vbLf &
                "  {""NCT_ID"":""NCT0"",""Criterion"":""Exclusion"",""Domain"":""Other"",""Concept"":""B"",""Qualifier"":"""",""TimeWindow"":"""",""OriginalText"":""Condition or situation which may put the subject at significant risk.""" & vbLf & "  ]")
        Dim result = _parser.ParseWithOutcome(broken, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(2, result.Records.Count)
        Assert.Equal(New String() {"A", "B"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
        Assert.Equal("Condition or situation which may put the subject at significant risk.",
                     result.Records(1).OriginalText)
    End Sub

    <Fact>
    Public Sub Repair_does_not_fire_on_array_properly_closed_with_brace_then_bracket()
        ' Safety: a well-formed array ends `..."}]` - a `}` sits between the last
        ' value's `"` and the `]`, so the value-quote-before-bracket pattern
        ' cannot match. Parses on the first attempt.
        Dim envelope = WrapEnvelope("text", CriterionArray(Crit(concept:="Adult")))
        Dim result = _parser.ParseWithOutcome(envelope, TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal("Adult", result.Records.Single().Concept)
    End Sub

    <Fact>
    Public Sub Repair_decodes_stringified_record_elements_with_unterminated_last()
        ' Reproduces NCT06689254 production failure: the model emitted records as
        ' stringified (double-encoded) JSON array elements, and the LAST one is
        ' unterminated (its closing wrapping quote was dropped before the array
        ' `]`), so the document fails to parse. The repair decodes each
        ' stringified element back into a real object - including the unterminated
        ' final one - so the whole batch parses.
        Dim normal = JsonSerializer.Serialize(Crit(concept:="Adult"))
        Dim s2 = JsonSerializer.Serialize(JsonSerializer.Serialize(Crit(concept:="B", criterion:="Exclusion")))
        Dim s3 = JsonSerializer.Serialize(JsonSerializer.Serialize(Crit(concept:="C", criterion:="Exclusion")))
        Dim sLastFull = JsonSerializer.Serialize(JsonSerializer.Serialize(Crit(concept:="D", criterion:="Exclusion")))
        Dim sLast = sLastFull.Substring(0, sLastFull.Length - 1)   ' drop the closing wrapping quote
        Dim payload = "[" & normal & "," & s2 & "," & s3 & "," & sLast & "]"
        Dim result = _parser.ParseWithOutcome(WrapEnvelope("text", payload), TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.True(result.WasRepaired)
        Assert.Equal(4, result.Records.Count)
        Assert.Equal(New String() {"Adult", "B", "C", "D"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Parse_recovers_stringified_record_element_in_clean_heterogeneous_array()
        ' Same double-encoding defect but every stringified element is properly
        ' terminated, so the array is valid heterogeneous JSON and parses on the
        ' first attempt. Without recovery the string elements would be silently
        ' dropped; EmitRecords decodes them, so no repair is needed.
        Dim normal = JsonSerializer.Serialize(Crit(concept:="Adult"))
        Dim stringified = JsonSerializer.Serialize(JsonSerializer.Serialize(Crit(concept:="B", criterion:="Exclusion")))
        Dim payload = "[" & normal & "," & stringified & "]"
        Dim result = _parser.ParseWithOutcome(WrapEnvelope("text", payload), TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Assert.Equal(2, result.Records.Count)
        Assert.Equal(New String() {"Adult", "B"},
                     result.Records.Select(Function(r) r.Concept).ToArray())
    End Sub

    <Fact>
    Public Sub Parse_ignores_plain_string_array_elements()
        ' Safety: a genuine (non-record) string element is not turned into a
        ' spurious record - recovery only fires when the decoded content is a
        ' JSON object carrying an NCT_ID.
        Dim normal = JsonSerializer.Serialize(Crit(concept:="Adult"))
        Dim payload = "[" & normal & ",""just a plain note""]"
        Dim result = _parser.ParseWithOutcome(WrapEnvelope("text", payload), TrialId)

        Assert.Equal(LlmParseResult.OutcomeSuccess, result.Outcome)
        Assert.False(result.WasRepaired)
        Dim record = Assert.Single(result.Records)
        Assert.Equal("Adult", record.Concept)
    End Sub

    ' ============ helpers ============

    Private Shared Function WrapEnvelope(fieldName As String, payload As String) As String
        Dim doc = New Dictionary(Of String, String) From {{fieldName, payload}}
        Return JsonSerializer.Serialize(doc)
    End Function

    Private Shared Function CriterionArray(ParamArray records As Object()) As String
        Return JsonSerializer.Serialize(records)
    End Function

    Private Shared Function Crit(
            Optional originalText As String = "Age >= 18",
            Optional concept As String = "Adult",
            Optional nctId As String = "",
            Optional criterion As String = "Inclusion",
            Optional domain As String = "Age",
            Optional qualifier As String = "",
            Optional timeWindow As String = "") As Object
        Return New Dictionary(Of String, String) From {
            {"NCT_ID", nctId},
            {"Criterion", criterion},
            {"Domain", domain},
            {"Concept", concept},
            {"Qualifier", qualifier},
            {"TimeWindow", timeWindow},
            {"OriginalText", originalText}
        }
    End Function

End Class
