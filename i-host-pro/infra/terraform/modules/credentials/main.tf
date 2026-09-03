# Secret CONTAINERS only. No aws_secretsmanager_secret_version is created by
# this module - populating a real value is a separate, explicit, later step
# (out of Terraform for values with no safe auto-generation source; a
# reviewed follow-up decision for values that could be Terraform-generated,
# e.g. database/RabbitMQ/Redis passwords - see CP5.3A report item 24/25).
resource "aws_secretsmanager_secret" "this" {
  for_each = toset(var.secret_names)

  name        = "ihostpro/${var.environment}/${each.value}"
  description = "iHostPro ${var.environment} - ${each.value} (container only, no value set by Terraform)"

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}
