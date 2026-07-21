Imports System.Collections.Generic
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Data
Imports Npgsql
Imports Xunit

' Pure-logic tests for PostgresGateway helpers and constructor validation.
' Anything that touches a real database lives in PostgresGatewayIntegrationTests.

Public Class PostgresGatewayUnitTests

    ' ============ ShouldSkipDistinctScan (filter-dropdown pg_stats pre-filter) ============
    '
    ' The rule guards a performance shortcut, so the tests that matter are the
    ' ones proving it stays conservative: an absent or borderline estimate must
    ' fall through to the real scan.

    <Fact>
    Public Sub ShouldSkipDistinctScan_skips_when_estimate_clears_cap_by_margin()
        ' 29605 distinct concepts against a cap of 100 - the production case.
        Assert.True(PostgresGateway.ShouldSkipDistinctScan(29605.0, 100))
    End Sub

    <Fact>
    Public Sub ShouldSkipDistinctScan_does_not_skip_when_estimate_is_unknown()
        ' 0 is pg_stats' "no statistics" encoding (never analyzed). An absent
        ' estimate is not evidence, so we must scan.
        Assert.False(PostgresGateway.ShouldSkipDistinctScan(0.0, 100))
        Assert.False(PostgresGateway.ShouldSkipDistinctScan(-1.0, 100))
    End Sub

    <Fact>
    Public Sub ShouldSkipDistinctScan_does_not_skip_below_or_at_the_margin()
        Dim cap = 100
        Dim margin = cap * PostgresGateway.DistinctSkipMarginFactor
        ' Comfortably under the cap: obviously scan.
        Assert.False(PostgresGateway.ShouldSkipDistinctScan(50.0, cap))
        ' Over the cap but inside the safety margin: the estimate is sampled and
        ' could be overstating, so scan rather than risk blanking a dropdown.
        Assert.False(PostgresGateway.ShouldSkipDistinctScan(101.0, cap))
        Assert.False(PostgresGateway.ShouldSkipDistinctScan(CDbl(margin), cap))
        ' Strictly past the margin: skip.
        Assert.True(PostgresGateway.ShouldSkipDistinctScan(CDbl(margin) + 1.0, cap))
    End Sub

    ' ============ MigrationNames ============

    <Fact>
    Public Sub MigrationNames_are_short_names_in_order_with_latest_last()
        Dim names = PostgresGateway.MigrationNames
        Assert.NotEmpty(names)
        ' Short names: namespace prefix and .sql suffix stripped.
        Assert.Equal("V1__schema", names(0))
        Assert.All(names, Sub(n)
                              Assert.DoesNotContain("EligibilityProcessing.Data.Migrations.", n)
                              Assert.DoesNotContain(".sql", n)
                          End Sub)
        ' The last entry is the current target schema level (drives the CLI
        ' migrate banner).
        Assert.Equal("V25__analytics_indexes", names(names.Count - 1))
    End Sub

    ' ============ NullIfEmpty ============

    <Fact>
    Public Sub NullIfEmpty_returns_DBNull_for_empty_string()
        Assert.Same(DBNull.Value, PostgresGateway.NullIfEmpty(""))
    End Sub

    <Fact>
    Public Sub NullIfEmpty_returns_DBNull_for_null()
        Assert.Same(DBNull.Value, PostgresGateway.NullIfEmpty(Nothing))
    End Sub

    <Fact>
    Public Sub NullIfEmpty_passes_non_empty_string_through()
        Assert.Equal("hello", PostgresGateway.NullIfEmpty("hello"))
    End Sub

    <Fact>
    Public Sub NullIfEmpty_preserves_whitespace_only_string()
        ' Whitespace is significant for some fields (e.g. OriginalText with leading spaces);
        ' we only collapse genuinely empty.
        Assert.Equal("   ", PostgresGateway.NullIfEmpty("   "))
    End Sub

    ' ============ NormalizeAactText (markdown-escape stripping) ============

    <Theory>
    <InlineData("FEV1 of \>= 60% of predicted", "FEV1 of >= 60% of predicted")>
    <InlineData("QRS \>/= 0.11", "QRS >/= 0.11")>
    <InlineData("age \< 65", "age < 65")>
    <InlineData("HER2\* positive", "HER2* positive")>
    <InlineData("section \[1\]", "section [1]")>
    <InlineData("normal text with no escapes", "normal text with no escapes")>
    <InlineData("Crohn\'s disease", "Crohn's disease")>
    <InlineData("R\&D unit referral", "R&D unit referral")>
    <InlineData("Hgb \>= 9.0 g/dL \& platelets \>= 100,000", "Hgb >= 9.0 g/dL & platelets >= 100,000")>
    Public Sub NormalizeAactText_strips_markdown_escape_backslashes(input As String, expected As String)
        Assert.Equal(expected, PostgresGateway.NormalizeAactText(input))
    End Sub

    <Fact>
    Public Sub NormalizeAactText_does_not_touch_letters_or_numbers_after_backslash()
        ' Only the CommonMark punctuation set is stripped. Backslashes before
        ' alphanumeric characters (e.g. \n as literal chars in pathological
        ' AACT data, never seen in practice but possible) survive untouched.
        Assert.Equal("path\name", PostgresGateway.NormalizeAactText("path\name"))
    End Sub

    <Theory>
    <InlineData(Nothing)>
    <InlineData("")>
    Public Sub NormalizeAactText_passes_empty_through(input As String)
        Assert.Equal(input, PostgresGateway.NormalizeAactText(input))
    End Sub

    ' ============ BuildMultiRowInsert (SQL shape and parameter binding) ============

    <Fact>
    Public Sub BuildMultiRowInsert_produces_one_value_tuple_per_record()
        Dim cmd = New NpgsqlCommand()
        Dim records = New ResolvedRecord() {
            MakeResolved(nctId:="NCT0", concept:="A"),
            MakeResolved(nctId:="NCT0", concept:="B"),
            MakeResolved(nctId:="NCT0", concept:="C")
        }
        PostgresGateway.BuildMultiRowInsert(cmd, records)

        Assert.Contains("INSERT INTO public.eligibility", cmd.CommandText)
        ' One '(' opens per VALUES tuple (plus one for the column list).
        Dim openParenCount = cmd.CommandText.Split("("c).Length - 1
        Assert.Equal(4, openParenCount)  ' column list + 3 value tuples
    End Sub

    <Fact>
    Public Sub BuildMultiRowInsert_binds_13_parameters_per_row()
        Dim cmd = New NpgsqlCommand()
        Dim records = New ResolvedRecord() {MakeResolved(), MakeResolved()}
        PostgresGateway.BuildMultiRowInsert(cmd, records)

        Assert.Equal(26, cmd.Parameters.Count)  ' 13 cols x 2 rows (semantic_type_tuis added in V22)
    End Sub

    <Fact>
    Public Sub BuildMultiRowInsert_uses_indexed_parameter_names_to_avoid_collisions()
        Dim cmd = New NpgsqlCommand()
        Dim records = New ResolvedRecord() {MakeResolved(), MakeResolved()}
        PostgresGateway.BuildMultiRowInsert(cmd, records)

        ' Each row's params are namespaced as p0_..., p1_...
        Dim names = cmd.Parameters.Cast(Of NpgsqlParameter).Select(Function(p) p.ParameterName).ToList()
        Assert.Contains("p0_nct_id", names)
        Assert.Contains("p1_nct_id", names)
        Assert.Contains("p0_concept", names)
        Assert.Contains("p1_concept", names)
    End Sub

    <Fact>
    Public Sub BuildMultiRowInsert_converts_unresolved_empty_strings_to_DBNull()
        Dim cmd = New NpgsqlCommand()
        Dim records = New ResolvedRecord() {MakeResolved(unresolved:=True)}
        PostgresGateway.BuildMultiRowInsert(cmd, records)

        Dim conceptCodeParam = cmd.Parameters("p0_concept_code")
        Assert.Same(DBNull.Value, conceptCodeParam.Value)

        Dim umlsNameParam = cmd.Parameters("p0_umls_name")
        Assert.Same(DBNull.Value, umlsNameParam.Value)

        Dim matchSourceParam = cmd.Parameters("p0_match_source")
        Assert.Same(DBNull.Value, matchSourceParam.Value)

        Dim matchScoreParam = cmd.Parameters("p0_match_score")
        Assert.Equal(CDec(0.0), CDec(matchScoreParam.Value))  ' 0 is a value, not DBNull
    End Sub

    <Fact>
    Public Sub BuildMultiRowInsert_passes_resolved_fields_through_unchanged()
        Dim cmd = New NpgsqlCommand()
        Dim records = New ResolvedRecord() {MakeResolved(
                nctId:="NCT0001",
                concept:="Diabetes",
                conceptCode:="C0011860",
                umlsName:="Diabetes Mellitus",
                matchSource:="MSH",
                matchScore:=0.875)}
        PostgresGateway.BuildMultiRowInsert(cmd, records)

        Assert.Equal("NCT0001", cmd.Parameters("p0_nct_id").Value)
        Assert.Equal("Diabetes", cmd.Parameters("p0_concept").Value)
        Assert.Equal("C0011860", cmd.Parameters("p0_concept_code").Value)
        Assert.Equal("Diabetes Mellitus", cmd.Parameters("p0_umls_name").Value)
        Assert.Equal("MSH", cmd.Parameters("p0_match_source").Value)
        Assert.Equal(CDec(0.875), CDec(cmd.Parameters("p0_match_score").Value))
    End Sub

    <Fact>
    Public Sub BuildMultiRowInsert_includes_all_12_columns_in_order()
        Dim cmd = New NpgsqlCommand()
        PostgresGateway.BuildMultiRowInsert(cmd, New ResolvedRecord() {MakeResolved()})

        ' Spec section 2.8.1 column order, verified by substring search.
        Dim sql = cmd.CommandText
        For Each col In New String() {
                "nct_id", "criterion", "domain", "concept", "concept_code",
                "semantic_type", "qualifier", "time_window", "original_text",
                "umls_name", "match_score", "match_source"}
            Assert.Contains(col, sql)
        Next
    End Sub

    ' ============ constructor validation ============

    <Fact>
    Public Sub Constructor_throws_on_null_outputDataSource()
        Assert.Throws(Of ArgumentNullException)(
            Function() New PostgresGateway(outputDataSource:=Nothing))
    End Sub

    ' ============ SelectNextTrialsAsync requires source DS ============

    <Fact>
    Public Async Function SelectNextTrials_throws_when_source_DS_not_configured() As System.Threading.Tasks.Task
        ' Use a connection string that points nowhere — we never actually open it
        ' because the input gate / source-check trips before any I/O.
        Dim outputDs = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d")
        Try
            Dim gateway = New PostgresGateway(outputDs)
            Await Assert.ThrowsAsync(Of InvalidOperationException)(
                Function() gateway.SelectNextTrialsAsync(
                    Array.Empty(Of String)(),
                    TrialSelectionDirection.Forward,
                    10,
                    System.Threading.CancellationToken.None))
        Finally
            outputDs.Dispose()
        End Try
    End Function

    ' ============ helpers ============

    Private Shared Function MakeResolved(
            Optional nctId As String = "NCT00000001",
            Optional concept As String = "Test",
            Optional conceptCode As String = "C0011860",
            Optional umlsName As String = "Test Concept",
            Optional matchSource As String = "MSH",
            Optional matchScore As Double = 0.75,
            Optional unresolved As Boolean = False) As ResolvedRecord
        Dim criterion = New CriterionRecord(
                nctId:=nctId,
                criterion:="Inclusion",
                domain:="Disease",
                concept:=concept,
                qualifier:="",
                timeWindow:="",
                originalText:="some original text")
        Dim match As UmlsMatch
        If unresolved Then
            match = UmlsMatch.Unresolved
        Else
            match = New UmlsMatch(conceptCode, umlsName, matchSource, matchScore)
        End If
        Return New ResolvedRecord(criterion, match, semanticTypes:=Array.Empty(Of SemanticTypeAssignment)())
    End Function

End Class
