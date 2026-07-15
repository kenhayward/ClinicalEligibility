Imports System.Collections.Generic
Imports System.Linq
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options

' Wire-level OpenAI-compatible embeddings client (POST /v1/embeddings).
'
' Mirrors LlmClient: named HttpClient + Polly resilience wired at the DI
' composition root; transport failures surface as EmbeddingResult.Failure so a
' single failure degrades gracefully rather than aborting the caller; user
' cancellation is re-thrown.
'
' BaseUrl / ApiKey fall back to the LLM endpoint's values when the Embedding
' section leaves them blank — the same server typically serves both.

Public NotInheritable Class EmbeddingClient
    Implements IEmbeddingClient

    Private ReadOnly _httpClient As HttpClient
    Private ReadOnly _options As EmbeddingOptions
    Private ReadOnly _llmOptions As LlmOptions
    Private ReadOnly _logger As ILogger(Of EmbeddingClient)

    Public Sub New(
            httpClient As HttpClient,
            options As IOptions(Of EmbeddingOptions),
            llmOptions As IOptions(Of LlmOptions),
            logger As ILogger(Of EmbeddingClient))
        _httpClient = httpClient
        _options = options.Value
        _llmOptions = llmOptions.Value
        _logger = logger
    End Sub

    Public ReadOnly Property Model As String Implements IEmbeddingClient.Model
        Get
            Return If(_options.Model, "")
        End Get
    End Property

    Public Async Function EmbedAsync(
            text As String,
            cancellationToken As CancellationToken) As Task(Of EmbeddingResult) _
            Implements IEmbeddingClient.EmbedAsync

        ' Truncate to the model's input ceiling — embedding models reject input
        ' past their fixed sequence length (see EmbeddingOptions.MaxInputChars).
        ' Truncation silently drops the tail of the text from the vector, so warn
        ' when it happens: a corpus that routinely exceeds the cap is a signal to
        ' raise MaxInputChars or move to a longer-context model. (bge-large-en-v1.5
        ' caps at 512 tokens; a 32K-context model wants MaxInputChars = 0.)
        Dim input = If(text, "")
        If _options.MaxInputChars > 0 AndAlso input.Length > _options.MaxInputChars Then
            _logger.LogWarning(
                "Embedding input truncated from {OriginalChars} to {MaxInputChars} chars; " &
                "the tail is excluded from the vector. Raise Embedding:MaxInputChars or use a longer-context model.",
                input.Length, _options.MaxInputChars)
            input = input.Substring(0, _options.MaxInputChars)
        End If

        Dim payload = New With {
            .model = _options.Model,
            .input = input
        }
        Dim payloadJson = JsonSerializer.Serialize(payload)

        Try
            Using content As New StringContent(payloadJson, Encoding.UTF8, "application/json")
                Using requestMsg As New HttpRequestMessage(HttpMethod.Post, BuildEmbeddingsUrl()) With {.Content = content}
                    Dim apiKey = If(String.IsNullOrEmpty(_options.ApiKey), _llmOptions.ApiKey, _options.ApiKey)
                    If Not String.IsNullOrEmpty(apiKey) Then
                        requestMsg.Headers.Authorization = New AuthenticationHeaderValue("Bearer", apiKey)
                    End If
                    Using response = Await _httpClient.SendAsync(requestMsg, cancellationToken).ConfigureAwait(False)
                        If Not response.IsSuccessStatusCode Then
                            Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                            _logger.LogWarning("Embeddings endpoint returned {Status}", CInt(response.StatusCode))
                            Return EmbeddingResult.Failure($"HTTP {CInt(response.StatusCode)}: {Truncate(body, 300)}")
                        End If
                        Using stream = Await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(False)
                            Using doc = Await JsonDocument.ParseAsync(stream, cancellationToken:=cancellationToken).ConfigureAwait(False)
                                Return ParseEmbeddingResponse(doc.RootElement)
                            End Using
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            _logger.LogWarning(ex, "Embeddings call failed")
            Return EmbeddingResult.Failure(ex.Message)
        End Try
    End Function

    Friend Function BuildEmbeddingsUrl() As String
        Dim baseUrl = If(_options.BaseUrl, "")
        If String.IsNullOrWhiteSpace(baseUrl) Then baseUrl = If(_llmOptions.BaseUrl, "")
        baseUrl = baseUrl.TrimEnd("/"c)
        If String.IsNullOrEmpty(baseUrl) Then baseUrl = "http://localhost:8080/v1"
        Return $"{baseUrl}/embeddings"
    End Function

    Friend Shared Function ParseEmbeddingResponse(root As JsonElement) As EmbeddingResult
        If root.ValueKind <> JsonValueKind.Object Then
            Return EmbeddingResult.Failure("Response root was not a JSON object")
        End If

        Dim dataProp As JsonElement = Nothing
        If Not root.TryGetProperty("data", dataProp) OrElse dataProp.ValueKind <> JsonValueKind.Array Then
            Return EmbeddingResult.Failure("Response missing data array")
        End If

        Dim firstItem = dataProp.EnumerateArray().FirstOrDefault()
        If firstItem.ValueKind <> JsonValueKind.Object Then
            Return EmbeddingResult.Failure("Response data array was empty")
        End If

        Dim embeddingProp As JsonElement = Nothing
        If Not firstItem.TryGetProperty("embedding", embeddingProp) OrElse
           embeddingProp.ValueKind <> JsonValueKind.Array Then
            Return EmbeddingResult.Failure("Response item missing embedding array")
        End If

        Dim vector As New List(Of Single)
        For Each component In embeddingProp.EnumerateArray()
            If component.ValueKind <> JsonValueKind.Number Then
                Return EmbeddingResult.Failure("Embedding array contained a non-numeric element")
            End If
            vector.Add(component.GetSingle())
        Next

        If vector.Count = 0 Then
            Return EmbeddingResult.Failure("Embedding vector was empty")
        End If
        Return EmbeddingResult.Success(vector)
    End Function

    Private Shared Function Truncate(s As String, maxLen As Integer) As String
        If String.IsNullOrEmpty(s) Then Return ""
        If s.Length <= maxLen Then Return s
        Return s.Substring(0, maxLen) & "..."
    End Function

End Class
