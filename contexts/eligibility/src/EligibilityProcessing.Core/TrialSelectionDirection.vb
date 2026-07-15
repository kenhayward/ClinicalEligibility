' Direction the batch-select query walks ctgov.eligibilities.
'
' Forward:  ORDER BY nct_id ASC — earliest unprocessed trial first. Default
'           behaviour, matches the "ascending watermark" workflow that
'           drove the first 7.5k trial backlog.
' Recent:   ORDER BY nct_id DESC — most-recent unprocessed trial first.
'           Used to catch up on newly-registered trials without disturbing
'           the forward backlog.
'
' Both directions anti-join against the set of NCT_IDs that already have an
' eligibility_study row (any status). The watermark-as-cutoff approach the
' codebase used to use (`nct_id > MAX(nct_id)`) doesn't survive mixing the
' two — running a Recent batch would push MAX into the recent NCT_ID range
' and silently skip everything below for the next Forward batch.

Public Enum TrialSelectionDirection
    Forward
    Recent
End Enum
