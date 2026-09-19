# Hosting plaintextpantry.com on AWS

One arm64 EC2 box (`t4g.small`) running the production compose stack from
[`infra/deploy`](../deploy): Postgres 16, the PowerSync service, the F# API
and Caddy (auto-HTTPS, serves the static client). Roughly $15/month.

```
GoDaddy (NS) ──▶ Route53 zone ──▶ Elastic IP
                                     │
                              ┌──────┴──────┐  EC2 t4g.small, Amazon Linux 2023
                              │    caddy    │  :80/:443  Let's Encrypt
                              │  /api/* ────┼──▶ server   (ECR image)
                              │  /powersync ┼──▶ powersync ──▶ postgres
                              │  /*  static │                    │
                              └─────────────┘         /data (EBS, daily snapshots)
```

| File | What it provisions |
|---|---|
| `bootstrap/` | Terraform state bucket + GitHub OIDC deploy role. Applied once, locally. |
| `dns.tf` | Route53 hosted zone, `A` records for apex and `www` |
| `network.tf` | VPC, public subnet, security group (80/443 only — no SSH) |
| `ec2.tf`, `user-data.sh` | The instance, Elastic IP, first-boot Docker install |
| `storage.tf` | Persistent EBS data volume (`prevent_destroy`), daily DLM snapshots, S3 bucket for deploy bundles |
| `ecr.tf` | Image repositories `plaintextpantry/server` and `plaintextpantry/web` |
| `secrets.tf` | Generated Postgres password and PowerSync JWT secret → SSM Parameter Store |
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

Commit the `.terraform.lock.hcl` files (already present) so CI and local
runs use the same provider builds.

## What a merge to `main` does

Each stage detects its own changes, so a run that changes nothing changes
nothing:

- **`infra`** — `terraform plan -detailed-exitcode`; `apply` only runs when
  the plan is non-empty. The plan output in the job log is the diff.
- **`build`** — images are tagged with `git rev-parse HEAD:app`, the tree hash
  of `app/`. Same source ⇒ same tag ⇒ the build is skipped when the tag already
  exists in ECR. Tags are immutable.
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
data directory is empty (first boot of the data volume). Alter existing
tables by hand through the SSM shell (`docker compose exec postgres psql -U
postgres pantry`), the same way local dev does.

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
