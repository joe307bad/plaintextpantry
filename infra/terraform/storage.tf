locals {
  az = "${var.aws_region}a"
}

# --- Persistent data volume --------------------------------------------------
# Postgres data and Caddy's certificates. Separate from the root volume so the
# instance can be replaced (new AMI, new instance type) without losing data.

resource "aws_ebs_volume" "data" {
  availability_zone = local.az
  size              = var.data_volume_size_gb
  type              = "gp3"
  encrypted         = true

  tags = {
    Name     = "plaintextpantry-data"
    Snapshot = "true" # DLM targets this tag
  }

  lifecycle {
    prevent_destroy = true
  }
}

# Daily EBS snapshots, 7 kept.
resource "aws_iam_role" "dlm" {
  name = "plaintextpantry-dlm"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Action    = "sts:AssumeRole"
      Principal = { Service = "dlm.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy_attachment" "dlm" {
  role       = aws_iam_role.dlm.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSDataLifecycleManagerServiceRole"
}

resource "aws_dlm_lifecycle_policy" "data" {
  description        = "Daily snapshots of the plaintextpantry data volume"
  execution_role_arn = aws_iam_role.dlm.arn
  state              = "ENABLED"

  policy_details {
    resource_types = ["VOLUME"]
    target_tags    = { Snapshot = "true" }

    schedule {
      name      = "daily"
      copy_tags = true

      create_rule {
        interval      = 24
        interval_unit = "HOURS"
        times         = ["06:00"]
      }

      retain_rule {
        count = 7
      }
    }
  }
}

# --- Deploy bundles ----------------------------------------------------------
# CI uploads infra/deploy (+ the shared powersync/postgres config) here as one
# tarball per commit; the box downloads it over SSM Run Command.

resource "aws_s3_bucket" "deploy" {
  bucket = "plaintextpantry-deploy-${data.aws_caller_identity.current.account_id}"
}

resource "aws_s3_bucket_public_access_block" "deploy" {
  bucket                  = aws_s3_bucket.deploy.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_lifecycle_configuration" "deploy" {
  bucket = aws_s3_bucket.deploy.id

  rule {
    id     = "expire-old-bundles"
    status = "Enabled"

    filter {
      prefix = "bundles/"
    }

    expiration {
      days = 30
    }
  }
}
