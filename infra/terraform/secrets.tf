# Secrets are generated here and stored in Parameter Store; deploy.sh on the
# box reads them into .env. They also live in Terraform state, which is why
# the state bucket is private and encrypted.

resource "random_password" "pg" {
  length  = 48
  special = false
}

resource "random_password" "powersync_jwt" {
  length  = 64
  special = false
}

resource "random_password" "keycloak_admin" {
  length  = 32
  special = false
}

# The confidential client the F# server signs in with. Chosen here and pushed
# into Keycloak by provision.sh, so nothing has to be read back out of it.
resource "random_password" "keycloak_client" {
  length  = 48
  special = false
}

locals {
  # The F# server signs PowerSync JWTs with the raw secret; the PowerSync
  # service verifies with its base64url encoding (see infra/docker/.env).
  powersync_jwt_b64url = replace(replace(replace(base64encode(random_password.powersync_jwt.result), "+", "-"), "/", "_"), "=", "")

  secrets = {
    PG_PASSWORD              = random_password.pg.result
    POWERSYNC_JWT_SECRET     = random_password.powersync_jwt.result
    POWERSYNC_JWT_SECRET_B64 = local.powersync_jwt_b64url
    KEYCLOAK_ADMIN_PASSWORD  = random_password.keycloak_admin.result
    KEYCLOAK_CLIENT_SECRET   = random_password.keycloak_client.result
  }
}

resource "aws_ssm_parameter" "secrets" {
  for_each = local.secrets

  name  = "${var.ssm_prefix}/${each.key}"
  type  = "SecureString"
  value = each.value
}

# Google OAuth client for Keycloak's Google identity provider. Created by hand
# in the Google Cloud console and written with:
#   aws ssm put-parameter --overwrite --type SecureString \
#     --name /plaintextpantry/AUTH_GOOGLE_ID --value '<client id>'
# (same for AUTH_GOOGLE_SECRET). Terraform only reserves the names; the
# "unset" placeholder is what deploy.sh treats as "no Google yet".
resource "aws_ssm_parameter" "google" {
  for_each = toset(["AUTH_GOOGLE_ID", "AUTH_GOOGLE_SECRET"])

  name  = "${var.ssm_prefix}/${each.key}"
  type  = "SecureString"
  value = "unset"

  lifecycle {
    ignore_changes = [value]
  }
}

# `/plaintextpantry/O11Y_API_KEY` - the write key for the observability box
# shared with fastbreak, which the server sends usage counters with - is
# deliberately NOT reserved here. It is the fastbreak app's key
# (`fly secrets list -a fastbreak-o11y`), it is written straight in:
#
#   aws ssm put-parameter --overwrite --type SecureString \
#     --name /plaintextpantry/O11Y_API_KEY --value '<key>'
#
# and a placeholder here would be a `value = "unset"` waiting to overwrite it
# on the first apply after somebody set it. deploy.sh reads it if it is there
# and counts nothing if it is not, which is the whole of what a reservation
# would have bought.
