variable "aws_region" {
  default = "us-east-1"
}

variable "domain" {
  default = "plaintextpantry.com"
}

variable "instance_type" {
  # ARM. Nothing is built on the box (images come from ECR), so 2 GB is
  # plenty for postgres + powersync + the API + caddy.
  default = "t4g.small"
}

variable "data_volume_size_gb" {
  description = "Persistent volume for Postgres and Caddy certs"
  default     = 20
}

variable "ssm_prefix" {
  description = "Parameter Store path the box reads secrets from"
  default     = "/plaintextpantry"
}
