Imports System.Threading
Imports System.Threading.Tasks

' Contract for turning a study's topic text into a vector embedding, used by
' the Authoring feature's similarity search (authoring specification §5.1).
'
' Lives in Core so consumers (the Web Authoring controller, the CLI backfill)
' do not depend on the transport library. EmbeddingClient implements this from
' the Llm project against an OpenAI-compatible /v1/embeddings endpoint.

Public Interface IEmbeddingClient

    ''' <summary>
    ''' The embedding model this client is configured to use. Recorded on each
    ''' eligibility_study_embedding row so similarity search only ever compares
    ''' same-model (and therefore same-dimension) vectors.
    ''' </summary>
    ReadOnly Property Model As String

    ''' <summary>
    ''' Embeds a single block of text. Transport failures are returned as
    ''' <see cref="EmbeddingResult.Failure"/>, not thrown — callers degrade
    ''' gracefully when the endpoint is unavailable. User cancellation is
    ''' re-thrown.
    ''' </summary>
    Function EmbedAsync(
            text As String,
            cancellationToken As CancellationToken) As Task(Of EmbeddingResult)

End Interface
