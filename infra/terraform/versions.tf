terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Bucket is created by ./bootstrap. Backend blocks can't take variables, so
  # the account id is spelled out. `use_lockfile` is S3-native locking (no
  # DynamoDB table), which is why >= 1.10 is required.
  backend "s3" {
    bucket       = "plaintextpantry-tfstate-372400261891"
    key          = "prod/terraform.tfstate"
    region       = "us-east-1"
    encrypt      = true
    use_lockfile = true
  }
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
