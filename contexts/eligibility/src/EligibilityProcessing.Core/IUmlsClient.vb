Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

' Contract for UMLS resolution, consumed by the orchestrator in Core.
'
' Lives in Core so the orchestrator does not have to depend on the transport
' library (EligibilityProcessing.Umls), preserving the dependency direction
' Umls -> Core. The concrete UmlsClient implements this interface from Umls.
'
' Both methods MUST swallow transport failures and return an empty result —
' UMLS errors are non-fatal per spec section 2.6.1 / 2.6.3. User cancellation
' (token signalled) MUST still propagate.

Public Interface IUmlsClient

    ''' <summary>
    ''' Spec section 2.6.1: GET /search/current with the canonical query params.
    ''' Returns up to <see cref="UmlsOptions.PageSize"/> candidates (typically 5).
    ''' Empty list on error, empty input, or no results.
    ''' </summary>
    Function SearchAsync(
            concept As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate))

    ''' <summary>
    ''' Spec section 2.6.3: GET /content/current/CUI/{cui}.
    ''' Returns the semantic-type assignments (TUI + name) for the given CUI.
    ''' Empty list on error or when the CUI has no semantic types.
    ''' </summary>
    ''' <remarks>
    ''' Returns pairs rather than names because the TUI is what
    ''' public.eligibility.semantic_type_tuis stores - TUIs are stable across
    ''' UMLS releases where names get reworded - while the display string is
    ''' built from the names. Returning only names would force one side to be
    ''' re-derived, which is how they drift.
    ''' </remarks>
    Function GetSemanticTypeAssignmentsAsync(
            cui As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of SemanticTypeAssignment))

End Interface
