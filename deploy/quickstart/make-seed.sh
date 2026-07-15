#!/usr/bin/env bash
# Produce the shippable seed asset: a data-only pg_dump of the six seed tables
# from a fully-inferenced source database. This is the maintainer counterpart to
# load-seed.sh - what it writes is exactly what the loader restores.
#
# The seed is DATA-ONLY on purpose: the migration framework owns the schema, so
# the loader runs `elig migrate` first and this dump only carries rows. It
# deliberately omits eligibility_study_embedding, the licensed umls.* schema, and
# PII (app_user / audit_log). See deploy/quickstart/README.md.
#
# Usage:
#   PGHOST=... PGPORT=... PGUSER=... PGPASSWORD=... PGDATABASE=clinical \
#     ./make-seed.sh [output-path]
# Default output: ./seed.dump
#
# After producing it, if it exceeds GitHub's 2GB per-asset limit, split it:
#   split -b 1900m seed.dump seed.dump.part-
# and publish the parts (in order) plus the sha256:
#   sha256sum seed.dump
set -euo pipefail

OUT="${1:-seed.dump}"

SEED_TABLES=(
  eligibility
  eligibility_study
  eligibility_study_detail
  eligibility_run
  eligibility_failed
  eligibility_umls_retry
)

table_args=()
for t in "${SEED_TABLES[@]}"; do
  table_args+=(-t "public.${t}")
done

echo "[make-seed] Dumping ${#SEED_TABLES[@]} tables (data only, -Fc) from ${PGDATABASE:-?} -> ${OUT}"
pg_dump --format=custom --data-only --no-owner --no-privileges \
  "${table_args[@]}" \
  --file="${OUT}"

echo "[make-seed] Done."
echo "[make-seed] Size:   $(du -h "${OUT}" | cut -f1)"
echo "[make-seed] sha256: $(sha256sum "${OUT}" | cut -d' ' -f1)"
echo
echo "If ${OUT} is > 2GB, split for GitHub:  split -b 1900m ${OUT} ${OUT}.part-"
