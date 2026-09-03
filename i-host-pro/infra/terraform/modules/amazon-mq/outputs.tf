output "amqps_hostname" {
  value = local.amqps_hostname
}

output "security_group_id" {
  value = aws_security_group.this.id
}
