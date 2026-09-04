# The validated certificate's ARN - only reaches ISSUED once the
# aws_acm_certificate_validation resource's DNS lookup succeeds, which
# requires the zone's nameservers to already be live at the registrar.
output "certificate_arn" {
  value = aws_acm_certificate_validation.this.certificate_arn
}
