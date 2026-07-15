Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options

' Wire-level client for the UMLS UTS REST API.
'
' Spec section 2.6.1 (search) and section 2.6.3 (semantic types). Both endpoints
' MUST continue on error and return empty results — the pipeline tolerates UMLS
' downtime as "criterion unresolved", never as a batch failure (section 6.4).
'
' HTTP transport (timeout, Polly retry, log redaction) is wired at the DI
' composition root via a named "umls" HttpClient. This class only owns the
' URL construction, response parsing, and error swallowing.

Public NotInheritable Class UmlsClient
    Implements IUmlsClient

    Private ReadOnly _httpClient As HttpClient
    Private ReadOnly _options As UmlsOptions
    Private ReadOnly _logger As ILogger(Of UmlsClient)

    Public Sub New(
            httpClient As HttpClient,
            options As IOptions(Of UmlsOptions),
            logger As ILogger(Of UmlsClient))
        _httpClient = httpClient
        _options = options.Value
        _logger = logger
    End Sub

    Public Async Function SearchAsync(
            concept As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate)) _
            Implements IUmlsClient.SearchAsync

        If String.IsNullOrWhiteSpace(concept) Then
            Return Array.Empty(Of UmlsCandidate)()
        End If

        Dim url = BuildSearchUrl(concept)
        Try
            Using response = Await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    _logger.LogWarning(
                            "UMLS /search returned {Status} for concept {Concept}",
                            CInt(response.StatusCode), concept)
                    Return Array.Empty(Of UmlsCandidate)()
                End If
                Using stream = Await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(False)
                    Using doc = Await JsonDocument.ParseAsync(stream, cancellationToken:=cancellationToken).ConfigureAwait(False)
                        Return ParseSearchResponse(doc.RootElement)
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogWarning(ex,
                    "UMLS /search failed for concept {Concept}; treating as empty result", concept)
            Return Array.Empty(Of UmlsCandidate)()
        End Try
    End Function

    Public Async Function GetSemanticTypesAsync(
            cui As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String)) _
            Implements IUmlsClient.GetSemanticTypesAsync

        If String.IsNullOrWhiteSpace(cui) Then
            Return Array.Empty(Of String)()
        End If

        Dim url = BuildSemanticTypesUrl(cui)
        Try
            Using response = Await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    _logger.LogWarning(
                            "UMLS /content/current/CUI returned {Status} for cui {Cui}",
                            CInt(response.StatusCode), cui)
                    Return Array.Empty(Of String)()
                End If
                Using stream = Await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(False)
                    Using doc = Await JsonDocument.ParseAsync(stream, cancellationToken:=cancellationToken).ConfigureAwait(False)
                        Return ParseSemanticTypesResponse(doc.RootElement)
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogWarning(ex,
                    "UMLS /content/current/CUI failed for cui {Cui}; treating as empty result", cui)
            Return Array.Empty(Of String)()
        End Try
    End Function

    ' --- URL construction ---

    Friend Function BuildSearchUrl(concept As String) As String
        Dim baseUrl = NormalizeBaseUrl()
        Return $"{baseUrl}/search/current" &
               $"?string={Uri.EscapeDataString(concept)}" &
               $"&searchType=words" &
               $"&returnIdType=concept" &
               $"&pageSize={_options.PageSize}" &
               $"&apiKey={Uri.EscapeDataString(_options.ApiKey)}"
    End Function

    Friend Function BuildSemanticTypesUrl(cui As String) As String
        Dim baseUrl = NormalizeBaseUrl()
        Return $"{baseUrl}/content/current/CUI/{Uri.EscapeDataString(cui)}" &
               $"?apiKey={Uri.EscapeDataString(_options.ApiKey)}"
    End Function

    Private Function NormalizeBaseUrl() As String
        Dim baseUrl = If(_options.BaseUrl, "").TrimEnd("/"c)
        If String.IsNullOrEmpty(baseUrl) Then baseUrl = "https://uts-ws.nlm.nih.gov/rest"
        Return baseUrl
    End Function

    ' --- response parsing ---

    Friend Shared Function ParseSearchResponse(root As JsonElement) As IReadOnlyList(Of UmlsCandidate)
        Dim list As New List(Of UmlsCandidate)
        If root.ValueKind <> JsonValueKind.Object Then Return list

        Dim resultProp As JsonElement = Nothing
        If Not root.TryGetProperty("result", resultProp) Then Return list
        Dim resultsProp As JsonElement = Nothing
        If Not resultProp.TryGetProperty("results", resultsProp) Then Return list
        If resultsProp.ValueKind <> JsonValueKind.Array Then Return list

        For Each element In resultsProp.EnumerateArray()
            If element.ValueKind <> JsonValueKind.Object Then Continue For
            Dim ui = GetStringOrEmpty(element, "ui")
            ' UMLS returns ui="NONE" with name="NO RESULTS" when nothing matches — skip.
            If String.IsNullOrEmpty(ui) OrElse
               String.Equals(ui, "NONE", StringComparison.OrdinalIgnoreCase) Then
                Continue For
            End If
            list.Add(New UmlsCandidate(
                    ui:=ui,
                    name:=GetStringOrEmpty(element, "name"),
                    rootSource:=GetStringOrEmpty(element, "rootSource")))
        Next
        Return list
    End Function

    Friend Shared Function ParseSemanticTypesResponse(root As JsonElement) As IReadOnlyList(Of String)
        Dim list As New List(Of String)
        If root.ValueKind <> JsonValueKind.Object Then Return list

        Dim resultProp As JsonElement = Nothing
        If Not root.TryGetProperty("result", resultProp) Then Return list
        Dim stsProp As JsonElement = Nothing
        If Not resultProp.TryGetProperty("semanticTypes", stsProp) Then Return list
        If stsProp.ValueKind <> JsonValueKind.Array Then Return list

        For Each element In stsProp.EnumerateArray()
            If element.ValueKind <> JsonValueKind.Object Then Continue For
            Dim name = GetStringOrEmpty(element, "name")
            If Not String.IsNullOrEmpty(name) Then list.Add(name)
        Next
        Return list
    End Function

    Private Shared Function GetStringOrEmpty(element As JsonElement, propertyName As String) As String
        Dim child As JsonElement = Nothing
        If Not element.TryGetProperty(propertyName, child) Then Return ""
        If child.ValueKind <> JsonValueKind.String Then Return ""
        Return If(child.GetString(), "")
    End Function

End Class
