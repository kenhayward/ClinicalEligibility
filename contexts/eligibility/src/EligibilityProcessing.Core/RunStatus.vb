' Status values for one row in public.eligibility_run - the per-batch record.
' Free text in the table (no CHECK constraint), enumerated here so callers stop
' scattering bare literals. These literals are PERSISTED: renaming one is a data
' migration.
'
'   running     - in flight; ended_at = Nothing
'   success     - the batch completed
'   failed      - the batch threw and did not complete
'   cancelled   - a user cancelled the batch mid-flight
'   interrupted - the host process stopped before the run reached a terminal
'                 status, leaving the row stranded at 'running'. NOT written by
'                 the pipeline. Unlike the per-study equivalent there is no
'                 automatic sweep: an operator resolves the row by hand from the
'                 Runs tab. See PostgresGateway.ResolveInterruptedRunAsync.

Imports System.Collections.Generic
Imports System.Linq

Public NotInheritable Class RunStatus

    Public Const Running As String = "running"
    Public Const Success As String = "success"
    Public Const Failed As String = "failed"
    Public Const Cancelled As String = "cancelled"
    Public Const Interrupted As String = "interrupted"

    ''' <summary>
    ''' Longest accepted manual reason. error_summary is rendered in a table
    ''' cell, so an unbounded paste would wreck the Runs tab.
    ''' </summary>
    Public Const MaxReasonLength As Integer = 500

    Private Shared ReadOnly _manualResolvable As String() = {Failed, Cancelled, Interrupted}

    ''' <summary>
    ''' The statuses an operator may manually resolve a stranded run TO.
    ''' Deliberately excludes Running (not a terminal state) and Success (a real
    ''' historical result that must never be manufactured by hand).
    ''' </summary>
    Public Shared ReadOnly Property ManualResolvable As IReadOnlyList(Of String)
        Get
            Return _manualResolvable
        End Get
    End Property

    ''' <summary>
    ''' Validates a manual resolution request. Pure - no I/O - so the controller
    ''' stays a thin shim and this logic is unit-testable without faking the
    ''' gateway. Trims and lower-cases the status, trims the reason.
    ''' </summary>
    Public Shared Function ValidateResolution(status As String, reason As String) As RunResolutionValidation
        Dim normalizedStatus = If(status, "").Trim().ToLowerInvariant()
        If Not _manualResolvable.Contains(normalizedStatus) Then
            Return RunResolutionValidation.Invalid(
                    $"status must be one of: {String.Join(", ", _manualResolvable)}")
        End If

        Dim normalizedReason = If(reason, "").Trim()
        If normalizedReason.Length = 0 Then
            Return RunResolutionValidation.Invalid("A reason is required.")
        End If
        If normalizedReason.Length > MaxReasonLength Then
            Return RunResolutionValidation.Invalid(
                    $"The reason must be {MaxReasonLength} characters or fewer.")
        End If

        Return RunResolutionValidation.Valid(normalizedStatus, normalizedReason)
    End Function

End Class

''' <summary>
''' Outcome of <see cref="RunStatus.ValidateResolution"/>. On success carries the
''' normalized status and reason ready to persist; on failure carries a message
''' safe to show the operator.
''' </summary>
Public NotInheritable Class RunResolutionValidation

    Private Sub New(isValid As Boolean, errorMessage As String, status As String, reason As String)
        Me.IsValid = isValid
        Me.ErrorMessage = errorMessage
        Me.Status = status
        Me.Reason = reason
    End Sub

    Public ReadOnly Property IsValid As Boolean
    Public ReadOnly Property ErrorMessage As String   ' "" when valid
    Public ReadOnly Property Status As String         ' canonical; "" when invalid
    Public ReadOnly Property Reason As String         ' trimmed; "" when invalid

    Friend Shared Function Valid(status As String, reason As String) As RunResolutionValidation
        Return New RunResolutionValidation(True, "", status, reason)
    End Function

    Friend Shared Function Invalid(errorMessage As String) As RunResolutionValidation
        Return New RunResolutionValidation(False, errorMessage, "", "")
    End Function

End Class
