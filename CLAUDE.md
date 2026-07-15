# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository status

**Fully implemented.** All 13 projects (8 `src`, 5 `tests`) build, and the extraction pipeline runs end-to-end - trial selection -> LLM extraction -> UMLS resolution -> per-trial persistence - behind a SignalR dashboard and a CLI, with ~650 passing unit tests (plus Postgres integration tests behind Docker). **Authentication, role-based authorization, and auditing** ship (cookie + Google sign-in with account linking; Owner / Administrator / Author / Viewer roles; first-run Owner bootstrap; Manage Accounts; and an audit trail with CSV export - see spec section 11). Not yet validated against the Run 75 production benchmark.

### Language deviation from the spec

Architecture section 1 specifies "VB.NET throughout", but Microsoft has not shipped VB.NET ASP.NET Core templates since .NET Core 3.x. The Web host (`EligibilityProcessing.Web`) is **C#**; everything else (6 libraries, CLI, 4 of 5 test projects) is **VB.NET**. `Microsoft.Extensions.DependencyInjection` lets the C# host compose the VB libraries with no friction.

Architecture section 2.7 / 2.8 originally split the trigger surface (`Webhook`) from the dashboard (`Web`) as separate hosts. The split has been collapsed: the Web host now serves both `POST /trigger` (with the original auth + rate limit + RunGate + BackgroundService plumbing) and the dashboard. Spec section 2.7 invocation semantics are preserved verbatim; only the deployment topology changed.

Test-project language tracks the system-under-test: `Integration.Tests` is C# (touches the web host), the four unit-test projects are VB.

### Repository layout

The pipeline lives under **`contexts/eligibility/`** (`src/` + `tests/` + `Eligibility.sln`). The `contexts/eligibility/` + repo-root `build/` layout is load-bearing: host projects import `..\..\..\..\build\Versioning.targets` (up 4 = repo root) and set `VersionJsonPath` to `..\..\version.json`. Keep that relative layout when moving projects.

## The spec documents are load-bearing

Read these before making changes that touch behaviour:

- [docs/specs/Eligibility_Processing_Specification.md](docs/specs/Eligibility_Processing_Specification.md) - technology-independent contract for the extraction pipeline. Captured verbatim from a production n8n workflow, validated against "Run 75". Treat its MUST/SHOULD wording literally; the numbered section-references below all point here.
- [docs/specs/Eligibility_Processing_DotNet_Architecture.md](docs/specs/Eligibility_Processing_DotNet_Architecture.md) - how the spec maps onto .NET 8 / VB.NET. Project layout, DI composition, NuGet picks, table schemas with the recommended additions applied.
- [docs/specs/database_schema.md](docs/specs/database_schema.md) - detailed reference for the output database schema (every table, column, index, FK) plus the AACT source tables read. Hand-maintained; see the schema-doc rule below.
- [docs/specs/configuration.md](docs/specs/configuration.md) - every configuration setting and where it is stored: the layered `appsettings.Shared.json` -> per-host `appsettings.json` -> env-var/`.env` override stack, the non-secret JSON tunables, and the secrets (connection strings, API keys, OAuth, trigger token) that live only in `.env`/user-secrets.

Section 6 of the architecture doc has an explicit spec-to-implementation mapping table - consult it before deciding where new code belongs.

**Schema-doc rule:** whenever you add or change a migration under `contexts/eligibility/src/EligibilityProcessing.Data/Migrations/` (new table, column, index, extension, constraint, or a drop), update [docs/specs/database_schema.md](docs/specs/database_schema.md) in the **same change** - the relevant table section, the migration-history table, and the extensions list if applicable. It describes the *effective* post-migration schema, so reflect drops/alters in place, not just append. A migration diff without a matching `database_schema.md` diff is incomplete work.

## What the system does (one-paragraph mental model)

Reads raw eligibility-criteria free text from AACT's `ctgov.eligibilities`, sends each trial to an LLM that returns a JSON array of discrete `(Criterion, Domain, Concept, Qualifier, TimeWindow, OriginalText)` records, looks each `Concept` up in UMLS to attach a CUI + semantic type when scoring confidence is high enough, and writes the result to `public.eligibility` with **per-trial DELETE+INSERT in its own transaction**. Progression is gated by the per-trial audit table - each batch anti-joins out every `NCT_ID` already in `public.eligibility_study` - which makes the system **trial-idempotent** and crash-resumable. (The original `MAX(NCT_ID)` watermark was dropped in migration V4; see architecture section 3.5.)

## Non-obvious constraints worth knowing up front

These are the rules most likely to be violated by well-intentioned changes:

- **Progress is the per-trial audit table, not a watermark** (section 2.2; architecture section 3.5). Each batch calls `GetAttemptedNctIdsAsync` (distinct `NCT_ID` from `public.eligibility_study`) and anti-joins that set inside `SelectNextTrialsAsync`. The `MAX(NCT_ID)` watermark and the `eligibility_watermark` table were removed in migration V4 - a `MAX` cutoff cannot survive mixing Forward and Recent batch directions. Do not reintroduce a standalone watermark.
- **Per-trial DELETE+INSERT in its own transaction is mandatory** (section 2.8.2). Not batch-level. Not append. This is what bounds blast radius and makes re-processing clean.
- **The empty-output "safety-net placeholder" is real** (section 2.5 step 9). When zero records survive parsing across a batch, the parser must emit a single record with all fields empty so the per-item topology does not collapse. The persistence stage then filters records with empty `NCT_ID`, dropping it. Removing the placeholder without removing the topology assumption silently drops legitimate batches; removing the filter persists garbage.
- **Pairing/correlation is end-to-end** (section 2.7). The "Pick Best Match" stage retrieves original criterion fields from the upstream parsed record *by paired index*, not from the UMLS response. Branching on `ConceptCode != ''` produces two streams that **must** be merged before persistence - failing to merge silently drops unresolved records.
- **UMLS match threshold is a hard cutoff at 0.45** (section 2.6.2). Records below threshold persist with empty `ConceptCode`/`UmlsName`/`MatchSource` and `MatchScore = 0`. The composite score is `max(levSim, jaccardContainment, acronymContribution)` - *max*, not sum or mean.
- **LLM extraction has no per-trial entry cap** (section 2.4.2). The original 25-entry cap was removed - the prompt now extracts every distinct criterion, bounded only by the model's token budget. Do not reintroduce a fixed cap without a spec change.
- **Source filtering excludes "please contact" / "contact site for" / "contact study"** (section 2.3). These filters live in the SELECT, not downstream.
- **Notifications are once-per-batch, not per-item** (section 2.10). The orchestrator enforces this, not the sink.
- **Three trigger modes converge on one pipeline** (section 2.1). Webhook is hard-coded to `StudyCount=500`; form/sub-workflow accept any integer; the default is 10.

## Reference benchmark to validate against

Run 75 (production n8n): 50 studies -> 374 rows, ~88% UMLS resolution, ~11 min wall clock, 8 in-flight LLM calls. A re-implementation passes if it lands within +/-15% row count and +/-3pp resolution rate on the same input (section 8). The full acceptance checklist is in spec section 8 and architecture section 7.

## Gaps the re-implementation is expected to close

These are known issues in the reference workflow that the .NET design addresses (architecture section 6 / spec section 9):

- Trials whose LLM call ultimately fails are never revisited -> `eligibility_failed` table + per-trial / multi-select re-run from the dashboard History tab.
- No primary key, index, or `created_at` on the output table -> migration adds all three.
- `MatchScore` typed as string in transit -> `numeric(4,3)` end-to-end.
- No UMLS lookup caching -> in-memory `UmlsCache` per run.
- Error notifications are binary "something failed" -> structured summary with NCT_IDs.

When you implement against the spec, prefer the architecture doc's resolved version over the spec's "as-is" reference.

## Legacy dead code

The pipeline still contains code from a since-removed study-authoring feature: the `public.authoring_*` tables (created by migrations V6/V10/V12/V13/V14) and their now-unreachable gateway methods + DTOs. This is dead code pending a cleanup that drops the tables and the code. Do not build new features on it.

## Testing discipline (project rule)

**Every new function ships with tests, and tests run on every build.**

- **Unit tests** live in the matching `contexts/eligibility/tests/EligibilityProcessing.<Name>.Tests` project. One test class per production class is a reasonable default. Cover the spec's numbered rules explicitly.
- **Integration tests** live in `contexts/eligibility/tests/EligibilityProcessing.Integration.Tests` and cover anything that crosses a boundary (DB, HTTP, hosted services). Prefer real Postgres + real wire-format fixtures over mocks; mocks belong in unit tests only.
- **When a function's behaviour changes, its tests change with it in the same commit.**
- **Verification is `dotnet test`, not `dotnet build`.** Before declaring any task complete, run `dotnet test contexts/eligibility/Eligibility.sln` and check that everything passes. A green build with red tests is not a passing change.
- **A new public function with no test is incomplete work.** This applies to interfaces too - interface contracts are tested via their implementations.

If a test is hard to write because the surrounding code is hard to test, that is a design signal - refactor for testability rather than skipping the test.

## Versioning (project rule)

The app is versioned as `MAJOR.MINOR.BUILD` with a release date in one committed JSON file, [contexts/eligibility/version.json](contexts/eligibility/version.json), the single source of truth that feeds the runtime About box / Release Notes, the build-time assembly stamp, and the docker image tag.

Shape: `{ app, current: { major, minor, build, releaseDate }, releases: [ { version, releaseDate, enhancements[], fixes[] } ] }` (releases newest-first). Keep it ASCII-only.

- **Every PR that touches the app bumps `current.build` by 1**, sets `current.releaseDate` to the PR date (`yyyy-mm-dd`), and **prepends a `releases` entry** describing the change. `releases[0]` must match `current`.
- **Any PR that adds a migration bumps at least the MINOR** (and resets `build` to 0) - schema version is tied to the minor. This pairs with the schema-doc rule above.
- **MAJOR** is a deliberate, manual bump for a breaking/architectural milestone.

The assembly stamp comes from [build/Versioning.targets](build/Versioning.targets) (imported by the host projects, which set `VersionJsonPath`); the runtime value is served at `GET /Version` + `/ReleaseNotes`; the docker image tag is read by `deploy/**/deploy*.ps1`. Editing `version.json` is the only step.

## Build / run / test

SDK is pinned to **8.0.318** via `global.json`. All commands run from repo root.

```powershell
dotnet test  contexts/eligibility/Eligibility.sln            # canonical verify: builds + runs all tests
dotnet build contexts/eligibility/Eligibility.sln            # build-only (rarely the right answer)
dotnet test  contexts/eligibility/tests/EligibilityProcessing.Core.Tests    # single test project
dotnet test  contexts/eligibility/Eligibility.sln --filter "FullyQualifiedName~LlmResponseParser"  # single class
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- run --count 10   # ad-hoc CLI run
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Cli -- embed-studies     # backfill the corpus similarity index
dotnet run --project contexts/eligibility/src/EligibilityProcessing.Web                       # local dashboard + trigger
```

Shared MSBuild props live in [Directory.Build.props](Directory.Build.props): `net8.0`, `Option Strict On` / `Option Infer On` for VB, `Nullable enable`. Per-project files inherit these - avoid duplicating the settings.

## Working tree notes

- Default branch: `main`. The `docs/` tree (specs) is tracked.
- **Always make changes on a branch and open a PR; never commit directly to `main`.** Branch from an up-to-date base: `git fetch` first and create the branch from the current `origin/main` tip, not from a possibly-stale local `main`.
- Platform is Windows (PowerShell). Use PowerShell syntax in commands (`$env:VAR`, not `$VAR`). The architecture assumes cross-platform deploy (Linux containers + Windows dev), so avoid OS-specific assumptions in code.
- **Never use em dashes. Always use a plain ASCII hyphen `-`.** More broadly, keep authored files **ASCII-only**: no em/en dashes, no curly/smart quotes, no ellipsis character, no other non-ASCII punctuation. Reason: Windows PowerShell 5.1 reads a BOM-less UTF-8 file as Windows-1252 and mangles those bytes, which has repeatedly broken `.ps1` parsing. This applies to source, scripts, config, comments, and commit messages.
