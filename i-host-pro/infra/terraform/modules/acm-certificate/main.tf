# CP5.3D-B item 9/10: DNS-validated ACM certificate, fully Terraform-managed
# - no manual CNAME creation required since Route53 is the delegated
# authority (item 10). The validation resource genuinely waits for AWS to
# see the CNAME resolve, which only happens after the user has swapped the
# Registro.br nameservers to this zone's - see item 37/38 in the CP5.3D-B
# gate: this is why the apply is meant to be split (zone first, then this).
resource "aws_acm_certificate" "this" {
  domain_name       = var.domain_name
  validation_method = "DNS"

  lifecycle {
    create_before_destroy = true
  }

  tags = {
    Project   = var.project
    ManagedBy = "Terraform"
  }
}

resource "aws_route53_record" "validation" {
  for_each = {
    for dvo in aws_acm_certificate.this.domain_validation_options : dvo.domain_name => {
      name   = dvo.resource_record_name
      type   = dvo.resource_record_type
      record = dvo.resource_record_value
    }
  }

  zone_id         = var.zone_id
  name            = each.value.name
  type            = each.value.type
  records         = [each.value.record]
  ttl             = 300
  allow_overwrite = true
}

resource "aws_acm_certificate_validation" "this" {
  certificate_arn         = aws_acm_certificate.this.arn
  validation_record_fqdns = [for record in aws_route53_record.validation : record.fqdn]
}
