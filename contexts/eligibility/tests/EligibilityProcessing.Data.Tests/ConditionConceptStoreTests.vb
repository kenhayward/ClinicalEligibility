Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Npgsql
Imports Xunit

' Integration tests for public.condition_concept (V24) and ConditionConceptStore.
Public Class ConditionConceptStoreTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    <SkippableFact>
    Public Async Function V24_creates_condition_concept_table_and_indexes() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'condition_concept'
ORDER BY column_name"
                Dim columns As New List(Of String)
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        columns.Add(reader.GetString(0))
                    End While
                End Using
                Assert.Contains("condition_norm", columns)
                Assert.Contains("raw_form", columns)
                Assert.Contains("study_count", columns)
                Assert.Contains("concept_code", columns)
                Assert.Contains("umls_name", columns)
                Assert.Contains("match_tier", columns)
                Assert.Contains("match_score", columns)
                Assert.Contains("resolved_at", columns)
                Assert.Contains("created_at", columns)
            End Using

            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT indexname FROM pg_indexes
WHERE schemaname = 'public' AND tablename = 'condition_concept'"
                Dim indexes As New List(Of String)
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        indexes.Add(reader.GetString(0))
                    End While
                End Using
                Assert.Contains("ix_condition_concept_code", indexes)
                Assert.Contains("ix_condition_concept_pending", indexes)
            End Using
        End Using
    End Function

    <SkippableFact>
    Public Async Function Sql_normalization_matches_ConceptKey_Normalize() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        Dim samples = {"COPD", "  Breast   Cancer  ", "Non-Small Cell", "COVID-19",
                       "Type" & vbTab & "2 Diabetes", "hiv/aids"}

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            For Each s In samples
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "SELECT regexp_replace(btrim(lower(@raw)), '\s+', ' ', 'g')"
                    cmd.Parameters.AddWithValue("raw", s)
                    Dim fromSql = CStr(Await cmd.ExecuteScalarAsync())
                    Assert.Equal(ConceptKey.Normalize(s), fromSql)
                End Using
            Next
        End Using
    End Function
End Class
