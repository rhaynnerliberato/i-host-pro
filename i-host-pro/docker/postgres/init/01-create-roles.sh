#!/usr/bin/env bash
# Provisions the two PostgreSQL roles the platform's least-privilege model
# requires (Incremento 1 plan, adendo final, Sections 6-7):
#
#   ihostpro_migrator — owns the schema/tables it creates via migrations
#   (IHostPro.MigrationRunner). Never used by Api/Worker.
#
#   ihostpro_app — used by IHostPro.Api / IHostPro.Worker at runtime.
#   Restricted to CONNECT + schema USAGE + CRUD on tables. Never CREATE,
#   ALTER, DROP, TRUNCATE, BYPASSRLS, and never a member of
#   ihostpro_migrator.
#
# Runs automatically only when the PostgreSQL container's data volume is
# empty (the official image's /docker-entrypoint-initdb.d/ convention). To
# (re)apply this in an environment where the volume already has data (e.g.
# rotating a password in homologation), copy this script into the running
# container and execute it manually with the same environment variables set,
# or run its psql invocation directly against the target host — it is
# idempotent and safe to re-run.
set -euo pipefail

: "${IHOSTPRO_APP_DB_PASSWORD:?IHOSTPRO_APP_DB_PASSWORD must be set}"
: "${IHOSTPRO_MIGRATOR_DB_PASSWORD:?IHOSTPRO_MIGRATOR_DB_PASSWORD must be set}"

# NOTE on a bug found and fixed during Incremento 1 homologation: psql's
# client-side variable substitution (`:'name'`) does NOT apply inside
# dollar-quoted regions (`$$ ... $$`). The original version of this script
# wrapped the CREATE/ALTER ROLE logic in `DO $$ ... $$` for idempotency,
# which silently defeated `:'migrator_password'`/`:'app_password'`
# substitution and sent the literal text to the server, causing a syntax
# error and leaving both roles uncreated. Fixed by keeping the conditional
# logic in a plain `SELECT ... \gexec` (never inside `$$ ... $$`), where psql
# substitution works normally and `format()` applies safe SQL quoting before
# `\gexec` executes the generated statement.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=migrator_password="$IHOSTPRO_MIGRATOR_DB_PASSWORD" \
  --set=app_password="$IHOSTPRO_APP_DB_PASSWORD" \
  --set=db_name="$POSTGRES_DB" <<'SQL'
-- %I quotes the role name as an identifier; %L quotes the password as a
-- safely-escaped string literal. Never concatenate shell/psql variables
-- directly into SQL text — format() is the only place a value is embedded.
--
-- Idempotent by construction: exactly one branch matches per role (CREATE
-- when absent, ALTER when present); ALTER only ever touches LOGIN/PASSWORD,
-- never DROPs a role, never revokes grants, never resets unrelated
-- attributes (CREATEDB/CREATEROLE/BYPASSRLS/membership stay whatever they
-- already were on an existing role).
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION',
              'ihostpro_migrator', :'migrator_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ihostpro_migrator')
UNION ALL
SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L',
              'ihostpro_migrator', :'migrator_password')
WHERE EXISTS (SELECT FROM pg_roles WHERE rolname = 'ihostpro_migrator')
UNION ALL
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION',
              'ihostpro_app', :'app_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ihostpro_app')
UNION ALL
SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L',
              'ihostpro_app', :'app_password')
WHERE EXISTS (SELECT FROM pg_roles WHERE rolname = 'ihostpro_app');
\gexec

-- ihostpro_migrator needs CREATE on the database itself to run a Bounded
-- Context's first-ever migration (CREATE SCHEMA <context>) — found and
-- fixed during Incremento 1 homologation ("permission denied for database
-- ihostpro"). Not specific to Identity: every future context's first
-- migration needs the same capability, so this belongs to role
-- provisioning, not any one context's migration. GRANT is inherently
-- idempotent (re-granting an already-held privilege is a no-op) — no
-- existence check needed. ihostpro_app deliberately receives no equivalent
-- grant; it must never be able to create a schema.
SELECT format('GRANT CREATE ON DATABASE %I TO %I', :'db_name', 'ihostpro_migrator')
\gexec
SQL
