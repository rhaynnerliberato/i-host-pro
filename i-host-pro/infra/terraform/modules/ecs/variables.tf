variable "environment" {
  type = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}

variable "aws_region" {
  type = string
}

variable "execution_role_arn" {
  type = string
}

variable "database_bootstrap_task_role_arn" {
  type = string
}

variable "migrationrunner_task_role_arn" {
  type = string
}

# CP5.3C corrective Decision Gate item 19: image_tag_mutability=IMMUTABLE on
# every ECR repo (CP5.2) rejects re-pushing to an existing tag, so "latest"
# is not a usable default here - a task definition needs a real, already-
# pushed git-SHA tag to be usable at all. No default: an empty/placeholder
# value would only produce a task definition nobody can actually run.
variable "database_bootstrap_image" {
  description = "Full ECR image reference (repository URL + immutable git-SHA tag) for the DatabaseBootstrap image."
  type        = string
}

variable "migrationrunner_image" {
  description = "Full ECR image reference (repository URL + immutable git-SHA tag) for the MigrationRunner image."
  type        = string
}

variable "database_bootstrap_security_group_id" {
  type = string
}

variable "migrationrunner_security_group_id" {
  type = string
}

variable "public_subnet_ids" {
  type = list(string)
}

variable "rds_master_user_secret_arn" {
  type = string
}

variable "database_app_secret_arn" {
  type = string
}

variable "database_migrator_secret_arn" {
  type = string
}

variable "rabbitmq_secret_arn" {
  type = string
}

variable "log_retention_days" {
  type    = number
  default = 30
}
