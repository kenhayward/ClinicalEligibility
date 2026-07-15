' Configuration for the embedding client (authoring specification §5.1).
' Bound from the "Embedding" configuration section.
'
' BaseUrl / ApiKey are optional — when blank, EmbeddingClient falls back to the
' LLM endpoint's BaseUrl / ApiKey, since the OpenAI-compatible server that
' serves /v1/chat/completions usually serves /v1/embeddings too.

Public Class EmbeddingOptions
    Public Property BaseUrl As String = ""
    Public Property ApiKey As String = ""
    Public Property Model As String = ""
    Public Property TimeoutSeconds As Integer = 30
    Public Property RetryCount As Integer = 2
    Public Property RetryDelaySeconds As Integer = 2

    ' Hard cap on the characters sent in one embedding request. Embedding
    ' models have a fixed maximum sequence length (e.g. bge-large-en-v1.5 is
    ' 512 tokens) and reject longer input outright. A study's topic is well
    ' captured by the start of its text, so the builder's output is truncated
    ' to this length. 1500 chars stays comfortably under a 512-token model;
    ' raise it for a long-context model. 0 disables the cap.
    Public Property MaxInputChars As Integer = 1500
End Class
