Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Cli       ' for Program.ParseStudyCount
Imports EligibilityProcessing.Core
Imports EligibilityProcessing.Data
Imports EligibilityProcessing.Hosting   ' for AddEligibilityPipeline extension
Imports EligibilityProcessing.Umls
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Xunit

' Wiring + smoke tests for the CLI composition root. These verify that:
'   1. Every service the orchestrator depends on resolves cleanly.
'   2. IUmlsClient resolves to the UmlsCache decorator, not the raw UmlsClient.
'   3. PostgresGateway is resolvable as both IPostgresGateway and the concrete
'      type (the CLI's migrate command uses the concrete type).
'   4. The migrate command (via EnsureSchemaAsync) actually creates the schema
'      end-to-end against a real Postgres testcontainer.

Public Class CliCompositionTests
    Implements IClassFixture(Of PostgresFixture)

    Private ReadOnly _fixture As PostgresFixture

    Public Sub New(fixture As PostgresFixture)
        _fixture = fixture
    End Sub

    ' ============ pure wiring (no DB) ============

    <Fact>
    Public Sub AddEligibilityPipeline_can_build_PipelineOrchestrator()
        Using provider = BuildServices().BuildServiceProvider()
            Using scope = provider.CreateScope()
                Dim orch = scope.ServiceProvider.GetRequiredService(Of PipelineOrchestrator)()
                Assert.NotNull(orch)
            End Using
        End Using
    End Sub

    <Fact>
    Public Sub IUmlsClient_resolves_to_the_UmlsCache_decorator()
        Using provider = BuildServices().BuildServiceProvider()
            Using scope = provider.CreateScope()
                Dim client = scope.ServiceProvider.GetRequiredService(Of IUmlsClient)()
                Assert.IsType(Of UmlsCache)(client)
            End Using
        End Using
    End Sub

    <Fact>
    Public Sub IPostgresGateway_and_PostgresGateway_resolve_to_the_same_instance()
        Using provider = BuildServices().BuildServiceProvider()
            Dim asInterface = provider.GetRequiredService(Of IPostgresGateway)()
            Dim asConcrete = provider.GetRequiredService(Of PostgresGateway)()
            Assert.Same(asInterface, asConcrete)
        End Using
    End Sub

    <Fact>
    Public Sub LlmResponseParser_and_UmlsMatchScorer_are_singletons()
        Using provider = BuildServices().BuildServiceProvider()
            Dim parserA = provider.GetRequiredService(Of LlmResponseParser)()
            Dim parserB = provider.GetRequiredService(Of LlmResponseParser)()
            Dim scorerA = provider.GetRequiredService(Of UmlsMatchScorer)()
            Dim scorerB = provider.GetRequiredService(Of UmlsMatchScorer)()
            Assert.Same(parserA, parserB)
            Assert.Same(scorerA, scorerB)
        End Using
    End Sub

    <Fact>
    Public Sub UmlsCache_is_scoped_not_singleton()
        Using provider = BuildServices().BuildServiceProvider()
            Using scopeA = provider.CreateScope()
                Using scopeB = provider.CreateScope()
                    Dim a = scopeA.ServiceProvider.GetRequiredService(Of IUmlsClient)()
                    Dim b = scopeB.ServiceProvider.GetRequiredService(Of IUmlsClient)()
                    Assert.NotSame(a, b)
                End Using
            End Using
        End Using
    End Sub

    ' ============ end-to-end (requires Docker) ============

    <SkippableFact>
    Public Async Function Migrate_command_creates_schema_against_real_postgres() As Task
        Skip.If(_fixture.SkipReason IsNot Nothing, _fixture.SkipReason)

        ' Build a fresh service provider pointing at the fixture's container,
        ' then exercise the migrate code path via the concrete gateway exactly
        ' the way Program.RunMigrateAsync does it.
        Dim config = New ConfigurationBuilder() _
            .AddInMemoryCollection(New Dictionary(Of String, String) From {
                {"Postgres:ConnectionStringSource", "Host=localhost;Port=1;Username=u;Password=p;Database=d"},
                {"Postgres:ConnectionStringOutput", _fixture.ConnectionString},
                {"Umls:ApiKey", "test"},
                {"Llm:ApiKey", "test"},
                {"Llm:BaseUrl", "http://localhost:8080/v1"}
            }) _
            .Build()

        Dim services As New ServiceCollection()
        services.AddLogging()
        services.AddEligibilityPipeline(config)
        Using provider = services.BuildServiceProvider()
            Dim gateway = provider.GetRequiredService(Of PostgresGateway)()
            Await gateway.EnsureSchemaAsync(CancellationToken.None)
        End Using

        Using conn = Await _fixture.DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT COUNT(*) FROM information_schema.tables
                    WHERE table_schema = 'public'
                      AND table_name IN ('eligibility','eligibility_run','eligibility_failed','eligibility_study')"
                Assert.Equal(4, Convert.ToInt32(Await cmd.ExecuteScalarAsync()))
            End Using
        End Using
    End Function

    ' ============ ParseStudyCount edge cases (Program.vb helper) ============

    <Theory>
    <InlineData(New String() {"run"}, 10)>
    <InlineData(New String() {"run", "--count", "5"}, 5)>
    <InlineData(New String() {"run", "--count=42"}, 42)>
    <InlineData(New String() {"run", "--count", "0"}, 10)>
    <InlineData(New String() {"run", "--count", "-3"}, 10)>
    <InlineData(New String() {"run", "--count", "abc"}, 10)>
    <InlineData(New String() {"run", "--count"}, 10)>
    Public Sub ParseStudyCount_handles_arg_variants(args As String(), expected As Integer)
        Assert.Equal(expected, Program.ParseStudyCount(args))
    End Sub

    ' ============ helpers ============

    Private Shared Function BuildServices() As IServiceCollection
        Dim config = New ConfigurationBuilder() _
            .AddInMemoryCollection(New Dictionary(Of String, String) From {
                {"Postgres:ConnectionStringSource", "Host=localhost;Port=1;Username=u;Password=p;Database=d"},
                {"Postgres:ConnectionStringOutput", "Host=localhost;Port=1;Username=u;Password=p;Database=d"},
                {"Umls:ApiKey", "test"},
                {"Llm:ApiKey", "test"},
                {"Llm:BaseUrl", "http://localhost:8080/v1"}
            }) _
            .Build()

        Dim services As New ServiceCollection()
        services.AddLogging()
        services.AddEligibilityPipeline(config)
        Return services
    End Function

End Class
