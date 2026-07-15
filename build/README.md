# build/ — cross-cutting build & CI assets

- `solution-filters/` — `*.slnf` per-context filters for fast, scoped builds.
- (CI definitions / scripts as they are added.)

Solutions: the root **`Platform.sln`** is the "everything" solution (CI / IDE / run-from-root convenience); **`contexts/eligibility/Eligibility.sln`** is the per-context solution. See [Protocol_Authoring_Platform_Design.md](../docs/Protocol_Authoring_Platform_Design.md) §3.4.
