Imports EligibilityProcessing.Llm
Imports Xunit

Public Class PromptBuilderTests

    ' ============ SystemPrompt (loaded from embedded resource) ============

    <Fact>
    Public Sub SystemPrompt_is_non_empty()
        Assert.False(String.IsNullOrWhiteSpace(PromptBuilder.SystemPrompt))
    End Sub

    <Fact>
    Public Sub SystemPrompt_mentions_json_array_format()
        Dim prompt = PromptBuilder.SystemPrompt
        Assert.Contains("JSON array", prompt, StringComparison.OrdinalIgnoreCase)
        Assert.Contains("[", prompt)
        Assert.Contains("]", prompt)
    End Sub

    <Fact>
    Public Sub SystemPrompt_lists_required_field_names()
        Dim prompt = PromptBuilder.SystemPrompt
        For Each field In New String() {"NCT_ID", "Criterion", "Domain", "Concept", "Qualifier", "TimeWindow", "OriginalText"}
            Assert.Contains(field, prompt)
        Next
    End Sub

    <Fact>
    Public Sub SystemPrompt_lists_inclusion_and_exclusion_criterion_values()
        Dim prompt = PromptBuilder.SystemPrompt
        Assert.Contains("Inclusion", prompt)
        Assert.Contains("Exclusion", prompt)
    End Sub

    <Theory>
    <InlineData("Disease")>
    <InlineData("Laboratory Test")>
    <InlineData("Surgery")>
    <InlineData("Drug Treatment")>
    <InlineData("Allergy")>
    <InlineData("Cardiovascular Function")>
    <InlineData("Reproductive Status")>
    <InlineData("Performance Status")>
    <InlineData("Infection")>
    <InlineData("Comorbidity")>
    <InlineData("Substance Use")>
    <InlineData("General Health")>
    <InlineData("Consent")>
    <InlineData("Age")>
    <InlineData("Sex")>
    <InlineData("Pregnancy")>
    <InlineData("Genetic")>
    <InlineData("Medical Device")>
    <InlineData("Imaging")>
    <InlineData("Vital Signs")>
    <InlineData("Mental Health")>
    <InlineData("Lifestyle")>
    <InlineData("Vaccination")>
    <InlineData("Other")>
    Public Sub SystemPrompt_lists_every_domain_value(domain As String)
        Assert.Contains(domain, PromptBuilder.SystemPrompt)
    End Sub

    <Fact>
    Public Sub SystemPrompt_does_not_impose_an_entry_cap()
        ' The historical 25-entry-per-trial cap was removed (spec section 2.4.2
        ' rule 1) — the prompt must extract every distinct criterion with no
        ' fixed maximum.
        Dim prompt = PromptBuilder.SystemPrompt
        Assert.Contains("no maximum", prompt, StringComparison.OrdinalIgnoreCase)
        Assert.DoesNotContain("25 entries", prompt)
    End Sub

    <Fact>
    Public Sub SystemPrompt_lists_all_supported_bullet_markers()
        Dim prompt = PromptBuilder.SystemPrompt
        For Each marker In New String() {"*", "-", "•", "·", "◦"}
            Assert.Contains(marker, prompt)
        Next
    End Sub

    <Fact>
    Public Sub SystemPrompt_includes_a_worked_example_with_a_real_looking_nct_id()
        ' Spec section 2.4.2: "The prompt MUST include at least one worked example
        ' showing a 3-row output for a real-looking trial."
        Dim prompt = PromptBuilder.SystemPrompt
        Assert.Matches("NCT\d{8}", prompt)
    End Sub

    <Fact>
    Public Sub SystemPrompt_is_cached_across_calls()
        Assert.Same(PromptBuilder.SystemPrompt, PromptBuilder.SystemPrompt)
    End Sub

    ' ============ BuildUserMessage (spec section 2.4.3) ============

    <Fact>
    Public Sub BuildUserMessage_uses_exact_format_from_spec()
        Dim result = PromptBuilder.BuildUserMessage("NCT00000123", "Inclusion: adult patients")
        Assert.Equal($"NCT_ID: NCT00000123{vbLf}Criteria:{vbLf}Inclusion: adult patients", result)
    End Sub

    <Fact>
    Public Sub BuildUserMessage_uses_lf_not_crlf_between_lines()
        ' Spec is line-oriented but doesn't specify line endings. Using LF only
        ' avoids subtle prompt-cache misses across platforms.
        Dim result = PromptBuilder.BuildUserMessage("NCT0", "x")
        Assert.DoesNotContain(vbCr, result)
        Assert.Contains(vbLf, result)
    End Sub

    <Fact>
    Public Sub BuildUserMessage_handles_null_nctId()
        Dim result = PromptBuilder.BuildUserMessage(Nothing, "criteria")
        Assert.StartsWith("NCT_ID: " & vbLf, result)
    End Sub

    <Fact>
    Public Sub BuildUserMessage_handles_null_criteria()
        Dim result = PromptBuilder.BuildUserMessage("NCT0", Nothing)
        Assert.EndsWith("Criteria:" & vbLf, result)
    End Sub

    <Fact>
    Public Sub BuildUserMessage_preserves_multiline_criteria_verbatim()
        Dim criteria = $"Inclusion:{vbLf}* age > 18{vbLf}Exclusion:{vbLf}* pregnant"
        Dim result = PromptBuilder.BuildUserMessage("NCT0", criteria)
        Assert.Contains(criteria, result)
    End Sub

    ' ============ Normalize prompt (Authoring §3.5) ============

    <Fact>
    Public Sub NormalizeSystemPrompt_is_non_empty()
        Assert.False(String.IsNullOrWhiteSpace(PromptBuilder.NormalizeSystemPrompt))
    End Sub

    <Fact>
    Public Sub NormalizeSystemPrompt_asks_for_one_canonical_statement()
        Dim prompt = PromptBuilder.NormalizeSystemPrompt
        Assert.Contains("canonical", prompt, StringComparison.OrdinalIgnoreCase)
    End Sub

    <Fact>
    Public Sub NormalizeSystemPrompt_is_cached_across_calls()
        Assert.Same(PromptBuilder.NormalizeSystemPrompt, PromptBuilder.NormalizeSystemPrompt)
    End Sub

    <Fact>
    Public Sub BuildNormalizeUserMessage_numbers_each_phrasing()
        Dim msg = PromptBuilder.BuildNormalizeUserMessage(
                New String() {"Adults over 18", "Patients aged 18 years or older"})
        Assert.Contains("1. Adults over 18", msg)
        Assert.Contains("2. Patients aged 18 years or older", msg)
    End Sub

    <Fact>
    Public Sub BuildNormalizeUserMessage_skips_blank_phrasings()
        Dim msg = PromptBuilder.BuildNormalizeUserMessage(
                New String() {"First", "   ", "Second"})
        Assert.Contains("1. First", msg)
        Assert.Contains("2. Second", msg)
        Assert.DoesNotContain("3.", msg)
    End Sub

End Class
