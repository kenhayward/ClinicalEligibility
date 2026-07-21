Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Data
Imports Npgsql
Imports Xunit

' Integration tests for the analytics reads (V25 indexes + AnalyticsGateway).
Public Class AnalyticsGatewayTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    <SkippableFact>
    Public Async Function V25_creates_both_analytics_indexes() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim indexes As New List(Of String)
        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT indexname FROM pg_indexes
WHERE schemaname = 'public' AND tablename = 'eligibility'"
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        indexes.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using

        Assert.Contains("ix_eligibility_concept_nct", indexes)
        Assert.Contains("ix_eligibility_nct_concept", indexes)
    End Function
End Class
