Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>The four resolution tiers stored in condition_concept.match_tier.</summary>
Public Module ConditionMatchTier
    ''' <summary>Exact umls.atom.str_norm match resolving to exactly one CUI.</summary>
    Public Const Exact As String = "exact"
    ''' <summary>Exact match resolving to several CUIs, tie-broken deterministically.</summary>
    Public Const ExactAmbiguous As String = "exact_ambiguous"
    ''' <summary>No exact atom; resolved by FTS/trigram search plus the scorer.</summary>
    Public Const Fuzzy As String = "fuzzy"
    ''' <summary>Attempted and rejected.</summary>
    Public Const Unresolved As String = "unresolved"
End Module

''' <summary>
''' The outcome of resolving one condition string.
'''
''' Deliberately NOT UmlsMatch: that type's MatchSource property means the root
''' source vocabulary (MSH, SNOMEDCT_US), whereas Tier here means which of the
''' three resolution paths produced the answer.
''' </summary>
Public NotInheritable Class ConditionResolution

    Public Sub New(conceptCode As String, umlsName As String, tier As String, score As Double)
        Me.ConceptCode = conceptCode
        Me.UmlsName = umlsName
        Me.Tier = tier
        Me.Score = score
    End Sub

    Public ReadOnly Property ConceptCode As String
    Public ReadOnly Property UmlsName As String
    Public ReadOnly Property Tier As String
    Public ReadOnly Property Score As Double

    Public ReadOnly Property IsResolved As Boolean
        Get
            Return Not String.IsNullOrEmpty(ConceptCode)
        End Get
    End Property

    Public Shared ReadOnly Unresolved As New ConditionResolution(
            conceptCode:="", umlsName:="", tier:=ConditionMatchTier.Unresolved, score:=0.0)

End Class

''' <summary>One row of public.condition_concept.</summary>
Public NotInheritable Class ConditionConceptEntry
    Public Property ConditionNorm As String = ""
    Public Property RawForm As String = ""
    Public Property StudyCount As Integer
    ''' <summary>Empty when unresolved.</summary>
    Public Property ConceptCode As String = ""
    Public Property UmlsName As String = ""
    Public Property MatchTier As String = ConditionMatchTier.Unresolved
    Public Property MatchScore As Double
End Class

''' <summary>
''' Data-access port for the condition dictionary. Implemented by
''' EligibilityProcessing.Data.ConditionConceptStore; faked in Core unit tests so
''' the tier logic can be tested without Postgres.
''' </summary>
Public Interface IConditionConceptStore

    ''' <summary>
    ''' Distinct CUIs whose atoms exactly match <paramref name="conditionNorm"/>
    ''' on umls.atom.str_norm, each carrying the concept's preferred name. Empty
    ''' when there is no exact atom.
    ''' </summary>
    Function LookupExactAsync(conditionNorm As String,
                              cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate))

    ''' <summary>Insert or update one dictionary row, stamping resolved_at.</summary>
    Function UpsertAsync(entry As ConditionConceptEntry,
                         cancellationToken As CancellationToken) As Task

    ''' <summary>
    ''' Raw condition strings on this study that have no dictionary row yet.
    ''' Empty (the steady-state case) means one indexed query and no work.
    ''' </summary>
    Function GetUnseenConditionsForStudyAsync(nctId As String,
                                              cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))

    ''' <summary>
    ''' Insert a dictionary row for every distinct normalized condition string in
    ''' the corpus that lacks one, and refresh raw_form + study_count on every
    ''' row. Returns the number of rows inserted. Idempotent.
    ''' </summary>
    Function SeedFromCorpusAsync(cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>
    ''' Rows still needing resolution, highest study_count first. When
    ''' <paramref name="force"/> is True every row is returned, not just those
    ''' with resolved_at IS NULL.
    ''' </summary>
    Function GetPendingAsync(limit As Integer, force As Boolean,
                             cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConditionConceptEntry))

    ''' <summary>Count matching GetPendingAsync, for the Tools card headline.</summary>
    Function CountPendingAsync(force As Boolean,
                               cancellationToken As CancellationToken) As Task(Of Integer)

End Interface
