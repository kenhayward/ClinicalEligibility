Imports System.Collections.Generic
Imports System.Linq

''' <summary>
''' Turns cohort and corpus concept counts into ranked distinctiveness rows.
''' Pure - no database, no I/O - so the ranking rule is unit-testable.
'''
''' Rows are ordered by EXCESS PERCENTAGE POINTS, not by lift. Lift saturates:
''' its maximum is corpusSize / cohortSize, and every concept that appears only
''' inside the cohort reaches that ceiling, so it cannot rank them. Measured on
''' a diabetes cohort the top eleven rows by lift all tied at 9.82, putting
''' "insulin pen injector" and "recurrent severe manic episodes" alongside real
''' findings. Ordering by excess produced hypertension, BMI, cardiovascular
''' disease, smoking and HbA1c instead. See the design spec section 4.2.
'''
''' Lift is still computed and displayed, because the two answer different
''' questions - "how much more common" versus "how many times more common".
''' </summary>
Public NotInheritable Class LiftCalculator

    ''' <summary>
    ''' Concepts below this many cohort trials are dropped. 71.3% of corpus
    ''' concepts appear in five or fewer trials, so without a floor the ranking
    ''' fills with one-trial noise carrying enormous ratios.
    ''' </summary>
    Public Const DefaultMinimumSupport As Integer = 10

    Private Sub New()
        ' Static-only.
    End Sub

    Public Shared Function Build(
            cohortCounts As IReadOnlyList(Of ConceptCount),
            corpusCounts As IReadOnlyList(Of ConceptCount),
            cohortSize As Integer,
            corpusSize As Integer,
            prefNames As IReadOnlyDictionary(Of String, String),
            definingCodes As ISet(Of String),
            minimumSupport As Integer) As IReadOnlyList(Of ConceptLiftRow)

        If cohortCounts Is Nothing OrElse cohortCounts.Count = 0 Then
            Return Array.Empty(Of ConceptLiftRow)()
        End If
        ' A zero-size cohort has no rates to compute; nothing meaningful to say.
        If cohortSize <= 0 OrElse corpusSize <= 0 Then
            Return Array.Empty(Of ConceptLiftRow)()
        End If

        Dim corpusByCode As New Dictionary(Of String, Integer)
        If corpusCounts IsNot Nothing Then
            For Each c In corpusCounts
                corpusByCode(c.ConceptCode) = c.Trials
            Next
        End If

        Dim floor = Math.Max(1, minimumSupport)
        Dim rows As New List(Of ConceptLiftRow)

        For Each c In cohortCounts
            If c.Trials < floor Then Continue For

            Dim corpusTrials As Integer = 0
            corpusByCode.TryGetValue(c.ConceptCode, corpusTrials)

            Dim pctCohort = 100.0 * c.Trials / cohortSize
            Dim pctCorpus = 100.0 * corpusTrials / corpusSize

            ' A concept absent from the corpus counts cannot have a ratio. Report
            ' lift 0 rather than infinity or NaN, which no UI can render and no
            ' sort can order. Excess is still meaningful and is the sort key.
            Dim lift As Double = If(pctCorpus > 0.0, pctCohort / pctCorpus, 0.0)

            Dim name As String = Nothing
            If prefNames Is Nothing OrElse Not prefNames.TryGetValue(c.ConceptCode, name) Then
                name = Nothing
            End If

            rows.Add(New ConceptLiftRow(
                    conceptCode:=c.ConceptCode,
                    prefName:=If(String.IsNullOrEmpty(name), c.ConceptCode, name),
                    cohortTrials:=c.Trials,
                    corpusTrials:=corpusTrials,
                    pctCohort:=pctCohort,
                    pctCorpus:=pctCorpus,
                    excessPp:=pctCohort - pctCorpus,
                    lift:=lift,
                    definesCohort:=definingCodes IsNot Nothing AndAlso definingCodes.Contains(c.ConceptCode)))
        Next

        ' Excess first. Cohort trials then concept code break ties, so a re-run
        ' reproduces the same order.
        Return rows _
            .OrderByDescending(Function(r) r.ExcessPp) _
            .ThenByDescending(Function(r) r.CohortTrials) _
            .ThenBy(Function(r) r.ConceptCode, StringComparer.Ordinal) _
            .ToList()
    End Function

End Class
