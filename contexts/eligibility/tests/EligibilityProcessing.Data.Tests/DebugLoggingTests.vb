Imports System.Collections.Generic
Imports System.Linq
Imports EligibilityProcessing.Hosting
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports Xunit

' The debug-logging switch: can an operator actually SEE SQL and LLM/HTTP calls in
' `docker compose logs`, and does the default stay quiet?
'
' WHY THIS NEEDS TESTS AT ALL: the mechanism is genuinely counter-intuitive. A code
' AddFilter BEATS configuration for the same category (rules match most-specific-first,
' and CompositionRoot's run after the host binds the Logging section). So the previous
' unconditional `AddFilter("Npgsql", Warning)` made the category unreachable - no
' appsettings entry and no environment variable could turn it on, and nothing said so.
' These tests pin the fallback-not-override semantics that fixed it.
'
' Asserted against the real LoggerFilterOptions the DI container produces, not against
' the helper functions, because the bug was in how the rules COMPOSE.
Public Class DebugLoggingTests

    ' Minimum config for AddEligibilityPipeline to bind without touching a database.
    Private Shared Function BuildProvider(logLevels As Dictionary(Of String, String)) As ServiceProvider
        Dim settings As New Dictionary(Of String, String) From {
            {"Postgres:ConnectionStringOutput", "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d"},
            {"Postgres:ConnectionStringSource", "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d"},
            {"Llm:BaseUrl", "http://localhost:1/v1"},
            {"Llm:Model", "m"}}
        For Each kv In logLevels
            settings($"Logging:LogLevel:{kv.Key}") = kv.Value
        Next

        Dim cfg = New ConfigurationBuilder().AddInMemoryCollection(settings).Build()
        Dim services As New ServiceCollection()
        services.AddSingleton(Of IConfiguration)(cfg)
        services.AddLogging()
        services.AddEligibilityPipeline(cfg)
        Return services.BuildServiceProvider()
    End Function

    ' Does a code-level filter rule exist pinning this category? That rule is what makes
    ' the category unreachable from config.
    Private Shared Function HasCodePin(sp As ServiceProvider, category As String) As Boolean
        Dim opts = sp.GetRequiredService(Of IOptions(Of LoggerFilterOptions))().Value
        Return opts.Rules.Any(Function(r) String.Equals(r.CategoryName, category, StringComparison.Ordinal) AndAlso
                                          r.LogLevel.HasValue AndAlso r.LogLevel.Value = LogLevel.Warning)
    End Function

    ' Default: the chatty categories stay pinned, from any launch directory, exactly as
    ' before. Losing this floods every host - a parallel normalize-umls run especially.
    <Fact>
    Public Sub With_no_configuration_the_noisy_categories_are_pinned_to_warning()
        Using sp = BuildProvider(New Dictionary(Of String, String))
            Assert.True(HasCodePin(sp, "Npgsql"), "Npgsql should be pinned by default")
            Assert.True(HasCodePin(sp, "System.Net.Http.HttpClient"), "HttpClient should be pinned by default")
            Assert.True(HasCodePin(sp, "Polly"), "Polly should be pinned by default")
        End Using
    End Sub

    ' THE regression test. An explicit level for the category must win, or "show me the
    ' SQL" is impossible again.
    <Fact>
    Public Sub An_explicit_npgsql_level_disables_the_code_pin()
        Using sp = BuildProvider(New Dictionary(Of String, String) From {{"Npgsql", "Debug"}})
            Assert.False(HasCodePin(sp, "Npgsql"), "an explicit Npgsql level must beat the code pin")
            ' ...and the others are untouched: turning on SQL must not also flood HTTP.
            Assert.True(HasCodePin(sp, "System.Net.Http.HttpClient"))
            Assert.True(HasCodePin(sp, "Polly"))
        End Using
    End Sub

    <Fact>
    Public Sub An_explicit_httpclient_level_disables_only_that_code_pin()
        Using sp = BuildProvider(New Dictionary(Of String, String) From {
                {"System.Net.Http.HttpClient", "Information"}})
            Assert.False(HasCodePin(sp, "System.Net.Http.HttpClient"))
            Assert.True(HasCodePin(sp, "Npgsql"))
        End Using
    End Sub

    ' The single master switch the operator actually asked for.
    <Theory>
    <InlineData("Debug")>
    <InlineData("Trace")>
    <InlineData("debug")>
    Public Sub A_debug_or_trace_default_lifts_every_pin(level As String)
        Using sp = BuildProvider(New Dictionary(Of String, String) From {{"Default", level}})
            Assert.False(HasCodePin(sp, "Npgsql"), $"Default={level} should lift the Npgsql pin")
            Assert.False(HasCodePin(sp, "System.Net.Http.HttpClient"), $"Default={level} should lift the HttpClient pin")
            Assert.False(HasCodePin(sp, "Polly"), $"Default={level} should lift the Polly pin")
        End Using
    End Sub

    ' A normal Default must NOT lift the pins - otherwise every deployment floods.
    <Theory>
    <InlineData("Information")>
    <InlineData("Warning")>
    <InlineData("None")>
    Public Sub A_normal_default_leaves_the_pins_in_place(level As String)
        Using sp = BuildProvider(New Dictionary(Of String, String) From {{"Default", level}})
            Assert.True(HasCodePin(sp, "Npgsql"))
            Assert.True(HasCodePin(sp, "System.Net.Http.HttpClient"))
            Assert.True(HasCodePin(sp, "Polly"))
        End Using
    End Sub

    <Fact>
    Public Sub IsVerboseLoggingRequested_only_treats_debug_and_trace_as_verbose()
        Dim cfgWith = Function(level As String) As IConfiguration
                          Dim d As New Dictionary(Of String, String)
                          If level IsNot Nothing Then d("Logging:LogLevel:Default") = level
                          Return New ConfigurationBuilder().AddInMemoryCollection(d).Build()
                      End Function

        Assert.True(CompositionRoot.IsVerboseLoggingRequested(cfgWith("Debug")))
        Assert.True(CompositionRoot.IsVerboseLoggingRequested(cfgWith("Trace")))
        Assert.False(CompositionRoot.IsVerboseLoggingRequested(cfgWith("Information")))
        Assert.False(CompositionRoot.IsVerboseLoggingRequested(cfgWith("Warning")))
        Assert.False(CompositionRoot.IsVerboseLoggingRequested(cfgWith(Nothing)))
    End Sub

End Class
