-- V11: Application users for authentication + role-based authorization.
--
-- Lightweight custom auth (no ASP.NET Identity / EF Core) — the web host owns
-- cookie + Google OAuth sign-in and stores credentials/roles here, accessed via
-- PostgresGateway like every other table.
--
-- A row supports password login (password_hash), Google login (google_subject),
-- or both (account linking by email): both columns are nullable. role is plain
-- text matching the codebase's text-status convention — Owner|Administrator|
-- Author|Viewer. Owner and Administrator share permissions; the distinct value
-- is what lets the app protect the last Owner from deletion/demotion.
--
-- Case-insensitive uniqueness on user_name and email (functional lower() unique
-- indexes) backs login lookups and Google email-linking; google_subject is
-- unique when present so one Google identity maps to at most one account.
--
-- Idempotent — CREATE IF NOT EXISTS so re-running EnsureSchemaAsync is safe.

CREATE TABLE IF NOT EXISTS public.app_user (
    user_id        uuid        NOT NULL PRIMARY KEY,
    user_name      text        NOT NULL,            -- login "userid"
    email          text        NOT NULL,            -- match key for Google linking
    display_name   text        NOT NULL DEFAULT '',
    role           text        NOT NULL,            -- Owner|Administrator|Author|Viewer
    password_hash  text,                            -- null for Google-only accounts
    google_subject text,                            -- null for password-only accounts
    picture_url    text,
    is_active      boolean     NOT NULL DEFAULT true,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    last_login_at  timestamptz
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_user_name ON public.app_user (lower(user_name));
CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_email     ON public.app_user (lower(email));
CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_google    ON public.app_user (google_subject)
    WHERE google_subject IS NOT NULL;
