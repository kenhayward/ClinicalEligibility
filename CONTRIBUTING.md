# Contributing

Thanks for your interest in ClinicalEligibility. This is a .NET 8 pipeline
(mostly VB.NET, with a C# ASP.NET Core web host).

## Getting set up

See [`Installation.md`](Installation.md) for a full from-scratch runbook. The
short version:

```powershell
git clone https://github.com/kenhayward/ClinicalEligibility.git
cd ClinicalEligibility
dotnet build contexts/eligibility/Eligibility.sln
dotnet test  contexts/eligibility/Eligibility.sln
```

The four unit-test projects (Core / Data-pure-logic / Llm / Umls) run without any
external services. The Postgres integration tests use Testcontainers and skip
cleanly when Docker is not running.

## Ground rules

- **Branch and open a PR.** Do not push to `main`. Branch from an up-to-date
  `origin/main`.
- **Tests ship with code.** Every new function ships with tests in the same
  commit; a behaviour change updates its tests in the same commit. `dotnet test`
  (not `dotnet build`) is the bar for "done". A new public function with no test
  is incomplete.
- **The specs are load-bearing.** Read [`docs/specs/`](docs/specs/) before
  changing behaviour the specification describes. Treat its MUST/SHOULD wording
  literally.
- **Schema-doc rule.** A migration under
  `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/` must be paired
  with a matching diff to [`docs/specs/database_schema.md`](docs/specs/database_schema.md)
  in the same change.
- **Versioning.** A PR that touches the app bumps `current.build` in
  [`contexts/eligibility/version.json`](contexts/eligibility/version.json) and
  prepends a `releases` entry; a PR that adds a migration bumps at least the
  MINOR. See [`CLAUDE.md`](CLAUDE.md) for the full rule.
- **ASCII only.** No em/en dashes, curly quotes, or other non-ASCII punctuation in
  source, scripts, config, comments, or commit messages - use a plain `-`. Windows
  PowerShell 5.1 mangles non-ASCII bytes in BOM-less UTF-8 files, which has broken
  `.ps1` parsing.

## Reporting bugs and requesting features

Open a GitHub issue. For a pipeline bug, a trial's `NCT_ID` and the stored raw LLM
response (from the History tab) make it reproducible.

## Security

Do not open a public issue for a security vulnerability. See
[`SECURITY.md`](SECURITY.md).
