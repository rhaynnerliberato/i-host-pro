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

variable "rabbitmq_rotation_task_role_arn" {
  type = string
}

variable "tenant_provisioning_task_role_arn" {
  type = string
}

variable "homolog_scenario_provisioning_task_role_arn" {
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

variable "rabbitmq_rotation_image" {
  description = "Full ECR image reference (repository URL + immutable git-SHA tag) for the RabbitMqCredentialRotation image."
  type        = string
}

variable "tenant_provisioning_image" {
  description = "Full ECR image reference (repository URL + immutable git-SHA tag) for the TenantProvisioning image."
  type        = string
}

variable "homolog_scenario_provisioning_image" {
  description = "Full ECR image reference (repository URL + immutable git-SHA tag) for the HomologScenarioProvisioning image."
  type        = string
}

variable "database_bootstrap_security_group_id" {
  type = string
}

variable "migrationrunner_security_group_id" {
  type = string
}

variable "rabbitmq_rotation_security_group_id" {
  type = string
}

variable "public_subnet_ids" {
  type = list(string)
}

variable "rds_master_user_secret_arn" {
  type = string
}

# NON_SECRET_CONFIG (CP5.3C runtime-proof correction): the real,
# AWS-managed master secret carries only username/password - endpoint
# identity was never secret and must never be inferred from it.
variable "rds_host" {
  type = string
}

variable "rds_port" {
  type = number
}

variable "rds_database_name" {
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

# CP5.3D-C corrective Decision Gate: the exact tenant/admin identity to
# provision - never hardcoded in code, never invented here either. No
# default on any of these: an empty/placeholder value would silently
# provision a meaningless tenant, which is worse than an explicit
# "you must supply this" plan-time error (same principle already applied to
# every image_tag variable in this codebase).
variable "tenant_provisioning_admin_password_secret_arn" {
  type = string
}

variable "tenant_provisioning_tenant_slug" {
  type = string
}

variable "tenant_provisioning_tenant_name" {
  type = string
}

variable "tenant_provisioning_admin_email" {
  type = string
}

variable "tenant_provisioning_admin_full_name" {
  type = string
}

# CP5.3D-D corrective Decision Gate: the real tenant to seed the test
# fixture into (HomologSyntheticBusinessFixture=true) - never a second
# tenant, always the same ihostpro-homolog tenant CP5.3D-C already
# provisioned. No default: an empty/placeholder value would silently
# provision the fixture into a meaningless tenant.
variable "homolog_scenario_provisioning_tenant_id" {
  type = string
}
