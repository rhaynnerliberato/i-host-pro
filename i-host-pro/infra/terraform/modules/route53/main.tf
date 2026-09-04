# CP5.3D-B: hosted zone for the apex domain (ihostpro.com.br), registered at
# Registro.br - only the DNS authority is delegated to Route53 here, never
# the registrar itself (item 2: NOT a registrar transfer). One zone for the
# whole domain, never a separate sub-zone per environment (item 4) -
# homolog/production hostnames are just records within this one zone.
resource "aws_route53_zone" "this" {
  name = var.domain_name

  tags = {
    Project   = var.project
    ManagedBy = "Terraform"
  }
}
