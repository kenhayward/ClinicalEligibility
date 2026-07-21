Imports System.Collections.Generic

' Result types for the Analytics tab. Immutable with positional constructors,
' matching CriterionCluster - the established shape for a Core analytics result.

''' <summary>How a cohort of trials is defined.</summary>
Public Enum AnalyticsCohortKind
    ''' <summary>Trials whose criteria mention a concept (optionally its descendants).</summary>
    Concept
    ''' <summary>Trials whose conditions map to a concept (optionally its descendants).</summary>
    Condition
    ''' <summary>Trials with a given eligibility_study_detail.phase.</summary>
    Phase
    ''' <summary>Trials whose start_date falls in a given year.</summary>
    Year
End Enum

''' <summary>
''' A cohort request: which kind, and the single value that selects it. For
''' Concept and Condition the value is a CUI; for Phase a phase string such as
''' PHASE3; for Year a four-digit year.
''' </summary>
Public NotInheritable Class AnalyticsCohort

    Public Sub New(kind As AnalyticsCohortKind, value As String, includeDescendants As Boolean)
        Me.Kind = kind
        Me.Value = If(value, "")
        Me.IncludeDescendants = includeDescendants
    End Sub

    Public ReadOnly Property Kind As AnalyticsCohortKind
    Public ReadOnly Property Value As String

    ''' <summary>
    ''' Only meaningful for Concept and Condition. Phase and Year ignore it -
    ''' there is no hierarchy over a phase or a year.
    ''' </summary>
    Public ReadOnly Property IncludeDescendants As Boolean

End Class

''' <summary>One concept and the number of distinct trials mentioning it.</summary>
Public NotInheritable Class ConceptCount

    Public Sub New(conceptCode As String, trials As Integer)
        Me.ConceptCode = If(conceptCode, "")
        Me.Trials = trials
    End Sub

    Public ReadOnly Property ConceptCode As String
    Public ReadOnly Property Trials As Integer

End Class

''' <summary>
''' One row of the distinctiveness view. Sorted by <see cref="ExcessPp"/>
''' descending, NOT by <see cref="Lift"/> - see the design spec section 4.2.
''' </summary>
Public NotInheritable Class ConceptLiftRow

    Public Sub New(conceptCode As String, prefName As String,
                   cohortTrials As Integer, corpusTrials As Integer,
                   pctCohort As Double, pctCorpus As Double,
                   excessPp As Double, lift As Double,
                   definesCohort As Boolean)
        Me.ConceptCode = If(conceptCode, "")
        Me.PrefName = If(prefName, "")
        Me.CohortTrials = cohortTrials
        Me.CorpusTrials = corpusTrials
        Me.PctCohort = pctCohort
        Me.PctCorpus = pctCorpus
        Me.ExcessPp = excessPp
        Me.Lift = lift
        Me.DefinesCohort = definesCohort
    End Sub

    Public ReadOnly Property ConceptCode As String
    ''' <summary>From umls.concept.pref_name, never from eligibility.concept.</summary>
    Public ReadOnly Property PrefName As String
    Public ReadOnly Property CohortTrials As Integer
    Public ReadOnly Property CorpusTrials As Integer
    Public ReadOnly Property PctCohort As Double
    Public ReadOnly Property PctCorpus As Double

    ''' <summary>Percentage points by which the cohort exceeds the corpus. The sort key.</summary>
    Public ReadOnly Property ExcessPp As Double

    ''' <summary>
    ''' Ratio of the two rates. Displayed, not sorted on: lift saturates at
    ''' corpus_size / cohort_size and every concept appearing only inside the
    ''' cohort reaches that ceiling, so it cannot rank them.
    ''' </summary>
    Public ReadOnly Property Lift As Double

    ''' <summary>
    ''' True when this concept is the cohort's own defining concept or one of its
    ''' hierarchy descendants - such a row is tautological. Marked, not hidden.
    ''' Always False for Phase and Year cohorts.
    ''' </summary>
    Public ReadOnly Property DefinesCohort As Boolean

End Class

''' <summary>One year of the trend view.</summary>
Public NotInheritable Class TrendPoint

    Public Sub New(year As Integer, studiesThatYear As Integer,
                   trialsWithConcept As Integer, pctOfYear As Double, isPartial As Boolean)
        Me.Year = year
        Me.StudiesThatYear = studiesThatYear
        Me.TrialsWithConcept = trialsWithConcept
        Me.PctOfYear = pctOfYear
        Me.IsPartial = isPartial
    End Sub

    Public ReadOnly Property Year As Integer

    ''' <summary>
    ''' Denominator - processed studies started that year. Carried so a thin
    ''' year is self-evident rather than looking equal to a well-covered one.
    ''' </summary>
    Public ReadOnly Property StudiesThatYear As Integer

    Public ReadOnly Property TrialsWithConcept As Integer
    Public ReadOnly Property PctOfYear As Double

    ''' <summary>True for the current calendar year, which is a part-year by definition.</summary>
    Public ReadOnly Property IsPartial As Boolean

End Class

''' <summary>Everything the concept lookup view shows about one concept.</summary>
Public NotInheritable Class ConceptSummary

    Public Sub New(conceptCode As String, prefName As String, rootSource As String,
                   semanticTypes As String, ancestorCount As Integer, descendantCount As Integer,
                   trials As Integer, corpusTrials As Integer,
                   byPhase As IReadOnlyList(Of ConceptCount),
                   exampleCriteria As IReadOnlyList(Of String))
        Me.ConceptCode = If(conceptCode, "")
        Me.PrefName = If(prefName, "")
        Me.RootSource = If(rootSource, "")
        Me.SemanticTypes = If(semanticTypes, "")
        Me.AncestorCount = ancestorCount
        Me.DescendantCount = descendantCount
        Me.Trials = trials
        Me.CorpusTrials = corpusTrials
        Me.ByPhase = If(byPhase, CType(Array.Empty(Of ConceptCount)(), IReadOnlyList(Of ConceptCount)))
        Me.ExampleCriteria = If(exampleCriteria, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
    End Sub

    Public ReadOnly Property ConceptCode As String
    Public ReadOnly Property PrefName As String
    Public ReadOnly Property RootSource As String
    Public ReadOnly Property SemanticTypes As String

    ''' <summary>Rows in umls.concept_ancestor where this concept is the descendant. 0 means it cannot roll up.</summary>
    Public ReadOnly Property AncestorCount As Integer
    Public ReadOnly Property DescendantCount As Integer

    Public ReadOnly Property Trials As Integer
    Public ReadOnly Property CorpusTrials As Integer

    ''' <summary>Phase label to trial count. ConceptCount.ConceptCode carries the phase label here.</summary>
    Public ReadOnly Property ByPhase As IReadOnlyList(Of ConceptCount)

    ''' <summary>
    ''' Up to five real eligibility.criterion texts. The ONE place raw extracted
    ''' text is shown, and it is labelled as examples - never used as a concept
    ''' label, because one CUI carries over a thousand distinct extracted strings.
    ''' </summary>
    Public ReadOnly Property ExampleCriteria As IReadOnlyList(Of String)

End Class
