#!/bin/sh
# First-run seed loader for the standalone ClinicalEligibility database.
#
# Runs AFTER `elig migrate` has created the schema (compose orders this via
# depends_on: migrate -> service_completed_successfully). It restores the
# pre-inferenced seed DATA into the already-migrated tables, so OSS users skip
# months / ~1B tokens of LLM re-inference.
#
# Contract (see deploy/quickstart/README.md):
#   - The seed is DATA-ONLY (the migration framework owns the schema).
#   - It contains exactly the 6 seed tables and NOTHING else (no umls.*, no
#     eligibility_study_embedding, no PII: app_user / audit_log).
#   - It is a Postgres custom-format archive (pg_dump -Fc), optionally byte-split
#     into parts that this script re-assembles by concatenation.
#
# Idempotent: if eligibility_study already has rows it skips (set SEED_FORCE=1 to
# reload). Standard libpq env vars (PGHOST/PGPORT/PGUSER/PGPASSWORD/PGDATABASE)
# select the target DB.
set -eu

# The 6 seed tables, in the order the schema/anti-join cares about. Restore uses
# --disable-triggers so FK order does not actually matter, but we stay explicit.
SEED_TABLES="eligibility eligibility_study eligibility_study_detail eligibility_run eligibility_failed eligibility_umls_retry"

DUMP="${SEED_DUMP_PATH:-/tmp/seed.dump}"
: "${PGDATABASE:=clinical}"
export PGDATABASE

log() { echo "[seed-loader] $*"; }

# --- 1. wait for the DB (depends_on covers this, but guard for direct runs) ---
tries=0
until pg_isready -q; do
  tries=$((tries + 1))
  if [ "$tries" -ge 60 ]; then
    log "ERROR: database not ready after 60s (PGHOST=${PGHOST:-?})"
    exit 1
  fi
  sleep 1
done

# --- 2. idempotency guard ---
# The schema must exist already (migrate ran first). If the table is missing we
# fail loudly rather than silently skipping - that means the ordering is wrong.
if ! existing="$(psql -tAqc 'SELECT count(*) FROM public.eligibility_study' 2>/dev/null)"; then
  log "ERROR: public.eligibility_study not found - run 'elig migrate' before the loader."
  exit 1
fi
existing="$(printf '%s' "$existing" | tr -dc '0-9')"
if [ "${existing:-0}" -gt 0 ] && [ "${SEED_FORCE:-0}" != "1" ]; then
  log "Already seeded: ${existing} trials present in eligibility_study. Skipping (set SEED_FORCE=1 to reload)."
  exit 0
fi

# --- 3. resolve the seed archive: local file wins, else download URL parts ---
if [ -n "${SEED_FILE:-}" ]; then
  if [ ! -f "$SEED_FILE" ]; then
    log "ERROR: SEED_FILE=$SEED_FILE does not exist (is the bind mount correct?)."
    exit 1
  fi
  DUMP="$SEED_FILE"
  log "Using local seed file: $DUMP"
elif [ -n "${SEED_URL:-}" ]; then
  log "Downloading seed ($(printf '%s' "$SEED_URL" | wc -w) part(s)) -> $DUMP"
  : > "$DUMP"
  n=0
  for url in $SEED_URL; do
    n=$((n + 1))
    log "  part $n: $url"
    curl -fsSL --retry 3 --retry-delay 2 "$url" >> "$DUMP"
  done
else
  log "ERROR: neither SEED_FILE nor SEED_URL is set - nothing to load."
  log "       Set SEED_URL to the GitHub Release asset URL(s), or bind-mount a"
  log "       local seed and set SEED_FILE."
  exit 1
fi

# --- 4. optional integrity check ---
if [ -n "${SEED_SHA256:-}" ]; then
  log "Verifying sha256..."
  echo "${SEED_SHA256}  ${DUMP}" | sha256sum -c - || {
    log "ERROR: sha256 mismatch - refusing to restore a corrupt seed."
    exit 1
  }
fi

# --- 5. restore DATA-ONLY into the migrated tables ---
log "Restoring seed data (this can take a few minutes for the full corpus)..."
restore_args="--data-only --disable-triggers --no-owner --no-privileges --exit-on-error"
for t in $SEED_TABLES; do
  restore_args="$restore_args -t $t"
done
# shellcheck disable=SC2086
pg_restore $restore_args -d "$PGDATABASE" "$DUMP"

# --- 6. reset the one identity sequence a data-only load leaves behind ---
# Only public.eligibility.id is a bigserial; the other 5 tables use uuid/natural
# keys. Without this, the next INSERT collides with a restored id.
log "Resetting eligibility.id sequence..."
psql -q -c "SELECT setval('public.eligibility_id_seq', (SELECT COALESCE(MAX(id), 0) FROM public.eligibility) + 1, false);"

# --- 7. report ---
trials="$(psql -tAqc 'SELECT count(DISTINCT nct_id) FROM public.eligibility_study' | tr -dc '0-9')"
rows="$(psql -tAqc 'SELECT count(*) FROM public.eligibility' | tr -dc '0-9')"
log "Done. Seed loaded: ${trials} trials, ${rows} eligibility rows."
