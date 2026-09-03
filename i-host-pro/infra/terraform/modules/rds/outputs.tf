output "endpoint" {
  value = aws_db_instance.this.address
}

output "port" {
  value = aws_db_instance.this.port
}

output "security_group_id" {
  value = aws_security_group.this.id
}

output "master_user_secret_arn" {
  description = "AWS-managed master credential secret ARN (bootstrap-only - never a runtime credential for Api/Worker/MigrationRunner)."
  value       = aws_db_instance.this.master_user_secret[0].secret_arn
}

output "database_name" {
  description = "NON_SECRET_CONFIG - the master secret carries only username/password (CP5.3C runtime-proof correction), never the database name."
  value       = var.database_name
}
