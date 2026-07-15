-- V21 (eligibility): signing credential + credential aging on the shared app_user directory.
ALTER TABLE public.app_user ADD COLUMN IF NOT EXISTS signing_password_hash text;
ALTER TABLE public.app_user ADD COLUMN IF NOT EXISTS password_updated_at timestamptz;
ALTER TABLE public.app_user ADD COLUMN IF NOT EXISTS signing_password_updated_at timestamptz;
