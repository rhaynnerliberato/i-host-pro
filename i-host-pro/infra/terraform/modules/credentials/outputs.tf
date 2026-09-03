output "secret_arns" {
  description = "Map of short name -> secret ARN, for wiring into IAM policies/ECS task definitions."
  value       = { for k, v in aws_secretsmanager_secret.this : k => v.arn }
}
