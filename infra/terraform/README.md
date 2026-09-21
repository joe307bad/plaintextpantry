# Hosting plaintextpantry.com on AWS

One arm64 EC2 box (`t4g.small`) running the production compose stack from
[`infra/deploy`](../deploy): Postgres 16, the PowerSync service, Keycloak,
the F# API and Caddy (auto-HTTPS, serves the static client). Roughly
$15/month.

```
GoDaddy (NS) ──▶ Route53 zone ──▶ Elastic IP
                                     │
                              ┌──────┴──────┐  EC2 t4g.small, Amazon Linux 2023
                              │    caddy    │  :80/:443  Let's Encrypt
                              │  /api/* ────┼──▶ server   (ECR image) ──▶ postgres
                              │  /mcp ──────┼──▶ server                     ▲
                              │  /powersync ┼──▶ powersync ─────────────────┤
                              │  /*  static │                               │
                              │ auth.<dom> ─┼──▶ keycloak ──────────────────┘
                              └─────────────┘         /data (EBS, daily snapshots)
```

**Identity**: Keycloak at `auth.plaintextpantry.com` owns the users and
brokers Google sign-in ("Login with Google" goes straight to Google's account
chooser; Keycloak's own pages are never shown). The F# server signs users in
with OIDC (session cookie), mints per-user PowerSync tokens, and serves an
OAuth-protected MCP endpoint at `/mcp`. The one Keycloak page people do see
is the consent screen when connecting Claude; it uses the login theme in
`infra/keycloak/themes/plaintextpantry`. Locally, `./dev.sh` runs the same
Keycloak with a seeded `dev@localhost` user; the login button signs in as
that user directly (`DEV_AUTO_LOGIN`), so `/login` is still there to work on.

| File | What it provisions |
|---|---|
| `bootstrap/` | Terraform state bucket + GitHub OIDC deploy role. Applied once, locally. |
| `dns.tf` | Route53 hosted zone, `A` records for apex and `www` |
| `network.tf` | VPC, public subnet, security group (80/443 only — no SSH) |
| `ec2.tf`, `user-data.sh` | The instance, Elastic IP, first-boot Docker install |
| `storage.tf` | Persistent EBS data volume (`prevent_destroy`), daily DLM snapshots, S3 bucket for deploy bundles |
| `ecr.tf` | Image repositories `plaintextpantry/server` and `plaintextpantry/web` |
| `secrets.tf` | Generated Postgres password, PowerSync JWT secret, Keycloak admin password and client secret → SSM Parameter Store; placeholders for the Google OAuth client |
| `iam.tf` | Instance role: SSM, ECR pull, read its secrets and bundles |

## One-time setup

1. **Terraform ≥ 1.10.** Homebrew's `terraform` formula is frozen at 1.5.7;
   use HashiCorp's tap: `brew install hashicorp/tap/terraform`.

2. **Bootstrap** (with your own AWS credentials):

   ```sh
   cd infra/terraform/bootstrap
   terraform init && terraform apply
   ```

   Creates `plaintextpantry-tfstate-<account>` and the
   `plaintextpantry-github-deploy` role, which only `main` of
   `joe307bad/plaintextpantry` can assume. No AWS keys go into GitHub.

3. **Merge to `main`.** The [deploy workflow](../../.github/workflows/deploy.yml)
   applies the main stack, builds the images, and deploys. The `infra` job's
   `Outputs` step prints the zone's nameservers (or run `terraform output
   name_servers` here).

4. **Delegate the domain.** In GoDaddy → DNS → Nameservers → *Change* → *Enter my
   own nameservers*, paste the four `awsdns-*` names. Propagation takes
   minutes to a few hours. Caddy keeps retrying Let's Encrypt until the name
   resolves to the box, then the site is live at https://plaintextpantry.com.

5. **Google sign-in.** Nobody can sign in to prod until this is done
   (Keycloak has no users of its own there). In the
   [Google Cloud console](https://console.cloud.google.com/apis/credentials):
   *Create credentials → OAuth client ID → Web application*, with the
   authorised redirect URI

   ```
   https://auth.plaintextpantry.com/realms/plaintextpantry/broker/google/endpoint
   ```

   (configure the OAuth consent screen first if the project has none). Then
   store the client id and secret and redeploy:

   ```sh
   aws ssm put-parameter --overwrite --type SecureString --name /plaintextpantry/AUTH_GOOGLE_ID     --value '<client id>'
   aws ssm put-parameter --overwrite --type SecureString --name /plaintextpantry/AUTH_GOOGLE_SECRET --value '<client secret>'
   gh workflow run deploy.yml
   ```

   `deploy.sh` re-provisions the realm on every deploy, so the identity
   provider appears (and the password form disappears) on the next run.

Commit the `.terraform.lock.hcl` files (already present) so CI and local
runs use the same provider builds.

## Connecting Claude (MCP)

Add a custom connector in Claude with the URL `https://plaintextpantry.com/mcp`.
Claude reads `/.well-known/oauth-protected-resource/mcp`, registers itself
with Keycloak (anonymous dynamic client registration is open to `claude.ai`),
sends you through Google, and shows a consent screen listing the scopes
(`recipes:read`, `recipes:write`, `shopping:read`, `shopping:write`,
`menus:read`, `menus:write`). The
tools then act as your account only. Locally the same works against
`http://localhost:5050/mcp` with the dev user.

## What a merge to `main` does

Each stage detects its own changes, so a run that changes nothing changes
nothing:

- **`infra`** — `terraform plan -detailed-exitcode`; `apply` only runs when
  the plan is non-empty. The plan output in the job log is the diff.
- **`build`** — images (`infra/docker/images/Dockerfile.*`, context `app/`)
  are tagged with a hash of the `app/` and `infra/docker/images/` git trees.
  Same source ⇒ same tag ⇒ the build is skipped when the tag already exists
  in ECR. Tags are immutable.
- **`deploy`** — `infra/deploy` (plus `infra/docker/powersync` and
  `infra/docker/postgres`, shared with local dev) is tarred to S3 and applied
  on the box over SSM Run Command by [`deploy.sh`](../deploy/deploy.sh).
  `docker compose up -d` recreates only containers whose image or config
  differs.

Changing sync rules, the Caddyfile or the compose file therefore redeploys
without rebuilding images; changing anything under `app/` rebuilds only the
images and leaves the infrastructure untouched.

## Day-to-day

```sh
# Shell on the box (needs the Session Manager plugin for the AWS CLI)
aws ssm start-session --target $(cd infra/terraform && terraform output -raw instance_id)

sudo -i
cd /opt/plaintextpantry
docker compose ps
docker compose logs -f server powersync
cat /var/log/user-data.log            # first-boot log
```

**Secrets** live in SSM under `/plaintextpantry/*` (and in Terraform state).
To rotate: `terraform apply -replace=random_password.pg` — then redeploy so
the box picks up the new `.env`. Note Postgres itself keeps the old password
in its data directory; change it with `ALTER USER` before rotating.

**Replacing the instance** (new AMI, new size): `terraform apply
-replace=aws_instance.main`. Data lives on the separate EBS volume, which
re-attaches to the new box; Caddy's certs are on it too, so no re-issuance.
Then re-run the workflow (`workflow_dispatch`) to deploy onto it.

**Restoring data**: pick a snapshot tagged `SnapshotCreator=DLM`, create a
volume from it in `us-east-1a`, and swap the volume id in
`aws_ebs_volume.data` via `terraform import` — or attach it by hand and
`rsync` `/data/postgres` across while the stack is down.

**Schema changes**: `infra/docker/postgres/init` only runs when the Postgres
data directory is empty (first boot of the data volume). Put re-runnable
statements in `infra/docker/postgres/migrate.sql`; `deploy.sh` and `dev.sh`
apply it on every start.

**Rows from before sign-in existed** have `user_id = ''` and are visible to
nobody. To hand them to an account, find its id (`/api/auth/me`, or the
Keycloak console) and, in the SSM shell:
`docker compose exec postgres psql -U postgres pantry -c "UPDATE recipes SET
user_id='<id>' WHERE user_id=''"` (same for `shopping_items`).

**Keycloak admin console**: `https://auth.plaintextpantry.com/admin/` as
`admin` with `aws ssm get-parameter --with-decryption --name
/plaintextpantry/KEYCLOAK_ADMIN_PASSWORD --query Parameter.Value --output text`.

**Local plan without CI**: `cd infra/terraform && terraform init && terraform
plan` uses your own credentials against the shared S3 state.

## Deliberate choices

- **Build in CI, not on the box.** Images are built on the GitHub runner and
  pulled from ECR, so the instance never needs the .NET SDK, Node, or the RAM
  a Fable + Vite build takes. The `Dockerfile`s compile on the runner's native
  arch and only `COPY` into the arm64 runtime stage — no emulated compiles.
- **No SSH.** The security group only opens 80/443; shells and deploys go
  through SSM using the instance role.
- **Caddy for TLS instead of ACM/ALB.** An ALB alone would cost more than
  the whole box. Caddy's certs persist on the data volume.
- **PowerSync under `/powersync` on the same origin** (prefix stripped by
  Caddy), so there is no CORS to configure and no extra certificate.
- **`max_slot_wal_keep_size=2GB`** on Postgres: a PowerSync redeploy can
  orphan its replication slot, and an uncapped slot pins WAL until the disk
  fills.
