-- V14: free-text reasoning for manually-added authored criteria.
--
-- Manually-added criteria carry no source-record lineage, so there is nowhere to
-- record why the author added them. This column captures that rationale; it is
-- surfaced in the criteria-tab expansion area (where source records would
-- normally show) and emitted in the eligibility audit CSV export.
--
-- Nullable so existing rows remain valid. Idempotent (ADD COLUMN IF NOT EXISTS)
-- so re-running EnsureSchemaAsync is safe. Mirrors the optional source_note
-- column added in V6.

ALTER TABLE public.authoring_criterion ADD COLUMN IF NOT EXISTS manual_reason text;
