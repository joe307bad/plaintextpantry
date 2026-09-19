# The zone is authoritative for the domain once GoDaddy's nameservers are
# pointed at the `name_servers` output. Caddy on the box handles TLS, so no
# ACM certificate is needed here.

resource "aws_route53_zone" "main" {
  name = var.domain
}

resource "aws_route53_record" "apex" {
  zone_id = aws_route53_zone.main.zone_id
  name    = var.domain
  type    = "A"
  ttl     = 300
  records = [aws_eip.main.public_ip]
}

resource "aws_route53_record" "www" {
  zone_id = aws_route53_zone.main.zone_id
  name    = "www.${var.domain}"
  type    = "A"
  ttl     = 300
  records = [aws_eip.main.public_ip]
}

# Keycloak. Caddy serves it on this name from the same box.
resource "aws_route53_record" "auth" {
  zone_id = aws_route53_zone.main.zone_id
  name    = "auth.${var.domain}"
  type    = "A"
  ttl     = 300
  records = [aws_eip.main.public_ip]
}
