output "zone_id" {
  value = aws_route53_zone.this.zone_id
}

# CP5.3D-B item 5/6: the ONLY manual step left for the user - swap the
# current Registro.br nameservers (a.auto.dns.br / b.auto.dns.br) for these
# 4 exact values, at Registro.br, after this zone is actually applied.
output "name_servers" {
  value = aws_route53_zone.this.name_servers
}
