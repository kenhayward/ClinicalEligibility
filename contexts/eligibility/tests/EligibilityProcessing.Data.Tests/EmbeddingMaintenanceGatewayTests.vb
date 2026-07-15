Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Xunit

' Integration tests for the embeddings maintenance gateway methods that back the
' owner-only export/import feature: GetEmbeddingStatsAsync (row count + per-model
' breakdown, surfaced so an imported set stays comparable for Find Similar) and
' ClearStudyEmbeddingsAsync (the truncate-before-import step).
'
' Run against the pgvector-enabled Postgres test container; skip cleanly when
' Docker is unavailable.

Public Class EmbeddingMaintenanceGatewayTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    Private Const EmbeddingDims As Integer = 1024

    Private Shared Function Embed(ParamArray values As Single()) As Single()
        Dim v(EmbeddingDims - 1) As Single
        Array.Copy(values, v, values.Length)
        Return v
    End Function

    <SkippableFact>
    Public Async Function GetEmbeddingStats_reports_total_and_per_model_counts() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        ' Two rows for model-a, one for model-b.
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync("NCT0001", Embed(1.0F), "model-a", "x", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync("NCT0002", Embed(0.5F), "model-a", "y", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync("NCT0003", Embed(0.2F), "model-b", "z", CancellationToken.None)

        Dim stats = Await _fixture.Gateway.GetEmbeddingStatsAsync(CancellationToken.None)

        Assert.Equal(3L, stats.TotalRows)
        Assert.Equal(2, stats.Models.Count)
        ' Ordered by count DESC, so model-a (2) leads model-b (1).
        Assert.Equal("model-a", stats.Models(0).Model)
        Assert.Equal(2L, stats.Models(0).Count)
        Assert.Equal("model-b", stats.Models(1).Model)
        Assert.Equal(1L, stats.Models(1).Count)
    End Function

    <SkippableFact>
    Public Async Function GetEmbeddingStats_is_empty_when_no_embeddings() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Dim stats = Await _fixture.Gateway.GetEmbeddingStatsAsync(CancellationToken.None)

        Assert.Equal(0L, stats.TotalRows)
        Assert.Empty(stats.Models)
    End Function

    <SkippableFact>
    Public Async Function ClearStudyEmbeddings_removes_all_rows_and_returns_the_count() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Await _fixture.Gateway.UpsertStudyEmbeddingAsync("NCT0001", Embed(1.0F), "m", "x", CancellationToken.None)
        Await _fixture.Gateway.UpsertStudyEmbeddingAsync("NCT0002", Embed(0.5F), "m", "y", CancellationToken.None)

        Dim cleared = Await _fixture.Gateway.ClearStudyEmbeddingsAsync(CancellationToken.None)
        Assert.Equal(2L, cleared)

        Dim after = Await _fixture.Gateway.GetEmbeddingStatsAsync(CancellationToken.None)
        Assert.Equal(0L, after.TotalRows)
    End Function

    <SkippableFact>
    Public Async Function ClearStudyEmbeddings_on_empty_returns_zero() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)
        Await _fixture.ResetAsync()

        Assert.Equal(0L, Await _fixture.Gateway.ClearStudyEmbeddingsAsync(CancellationToken.None))
    End Function
End Class
