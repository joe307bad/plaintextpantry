# One-time bootstrap, applied locally with your own AWS credentials. Creates
# the two things the main stack and CI need before they can run: the S3
# bucket that holds Terraform state, and the IAM role GitHub Actions assumes
# via OIDC (no long-lived AWS keys in GitHub).
#
# State for this module is a local file (gitignored). It holds three
# resources; if it is ever lost, `terraform import` them back.
#
#   terraform init && terraform apply

terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

variable "aws_region" {
  default = "us-east-1"
}

variable "github_repo" {
  description = "owner/name of the repository allowed to assume the deploy role"
  default     = "joe307bad/plaintextpantry"
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      Project   = "plaintextpantry"
      ManagedBy = "terraform"
    }
  }
}

data "aws_caller_identity" "current" {}

# --- Terraform state ---------------------------------------------------------

resource "aws_s3_bucket" "tfstate" {
  bucket = "plaintextpantry-tfstate-${data.aws_caller_identity.current.account_id}"

  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_s3_bucket_versioning" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "tfstate" {
  bucket = aws_s3_bucket.tfstate.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_public_access_block" "tfstate" {
  bucket                  = aws_s3_bucket.tfstate.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# --- GitHub Actions OIDC -----------------------------------------------------

resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
  # AWS validates GitHub's OIDC cert chain itself now; the thumbprint is only
  # required by the API schema.
  thumbprint_list = ["6938fd4d98bab03faadb97b34396831e3780aea1"]
}

# Only workflows running on `main` of this repo can assume the role. That
# covers push-to-main and workflow_dispatch on main, and nothing from PRs or
# forks.
resource "aws_iam_role" "github_deploy" {
  name = "plaintextpantry-github-deploy"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Action    = "sts:AssumeRoleWithWebIdentity"
      Principal = { Federated = aws_iam_openid_connect_provider.github.arn }
      Condition = {
        StringEquals = {
          "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
          "token.actions.githubusercontent.com:sub" = "repo:${var.github_repo}:ref:refs/heads/main"
        }
      }
    }]
  })
}

# The role runs `terraform apply` over the whole stack (VPC, IAM, EC2, Route53,
# ECR, SSM...). A least-privilege policy for that is a moving target every
# time a resource is added, so it gets admin; the trust policy above is the
# real boundary.
resource "aws_iam_role_policy_attachment" "github_deploy_admin" {
  role       = aws_iam_role.github_deploy.name
  policy_arn = "arn:aws:iam::aws:policy/AdministratorAccess"
}

# --- Outputs -----------------------------------------------------------------

output "state_bucket" {
  value = aws_s3_bucket.tfstate.bucket
}

output "deploy_role_arn" {
  value = aws_iam_role.github_deploy.arn
}
