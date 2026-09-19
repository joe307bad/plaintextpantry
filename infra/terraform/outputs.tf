output "name_servers" {
  description = "Set these as the nameservers for the domain at GoDaddy"
  value       = aws_route53_zone.main.name_servers
}

output "elastic_ip" {
  value = aws_eip.main.public_ip
}

output "instance_id" {
  value = aws_instance.main.id
}

output "ecr_registry" {
  value = "${data.aws_caller_identity.current.account_id}.dkr.ecr.${var.aws_region}.amazonaws.com"
}

output "deploy_bucket" {
  value = aws_s3_bucket.deploy.bucket
}

output "domain" {
  value = var.domain
}

output "ssm_prefix" {
  value = var.ssm_prefix
}

output "shell" {
  description = "Interactive shell on the box (no SSH; needs the session-manager-plugin)"
  value       = "aws ssm start-session --region ${var.aws_region} --target ${aws_instance.main.id}"
}
