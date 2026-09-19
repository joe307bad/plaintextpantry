# Latest Amazon Linux 2023 for arm64. Resolved at plan time, but the instance
# ignores AMI changes so a new monthly AMI never replaces the box on its own
# (replace deliberately: `terraform apply -replace=aws_instance.main`).
data "aws_ssm_parameter" "al2023_arm64" {
  name = "/aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-arm64"
}

resource "aws_instance" "main" {
  ami                    = data.aws_ssm_parameter.al2023_arm64.insecure_value
  instance_type          = var.instance_type
  subnet_id              = aws_subnet.public.id
  vpc_security_group_ids = [aws_security_group.web.id]
  iam_instance_profile   = aws_iam_instance_profile.instance.name

  root_block_device {
    volume_size = 16
    volume_type = "gp3"
    encrypted   = true
  }

  metadata_options {
    http_tokens = "required" # IMDSv2 only
  }

  user_data = templatefile("${path.module}/user-data.sh", {
    aws_region = var.aws_region
  })

  lifecycle {
    ignore_changes = [ami, user_data]
  }

  tags = { Name = "plaintextpantry" }
}

resource "aws_volume_attachment" "data" {
  device_name = "/dev/xvdf"
  volume_id   = aws_ebs_volume.data.id
  instance_id = aws_instance.main.id
}

resource "aws_eip" "main" {
  domain = "vpc"
  tags   = { Name = "plaintextpantry" }
}

resource "aws_eip_association" "main" {
  instance_id   = aws_instance.main.id
  allocation_id = aws_eip.main.id
}
