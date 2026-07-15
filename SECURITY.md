# Security Policy

## Reporting a vulnerability

Please **do not** report security vulnerabilities through public GitHub issues.

Instead, use GitHub's private vulnerability reporting for this repository
(**Security** tab -> **Report a vulnerability**), which opens a private advisory
visible only to the maintainers.

Please include:

- A description of the vulnerability and its impact.
- Steps to reproduce, or a proof of concept.
- Any affected configuration (e.g. which host, which auth path).

You can expect an initial acknowledgement within a few business days. We will keep
you informed as we work on a fix and coordinate disclosure.

## Scope and handling notes

- **Secrets never belong in the repo.** All secrets (database connection strings,
  LLM/UMLS API keys, OAuth client secret, the `Webhook__Secret` trigger token)
  live only in `.env` / environment variables / user-secrets. The repo ships only
  [`.env.example`](.env.example) with placeholders. If you find a real secret
  committed anywhere, report it privately as above.
- The UMLS client redacts `apiKey` query parameters from logs; keep that behaviour
  intact when touching UMLS request logging.
- The `POST /trigger` endpoint is anonymous by design but gated by the
  `Webhook__Secret` shared secret - when the secret is unset it rejects every
  request. Do not add an implicit "no auth" path.
