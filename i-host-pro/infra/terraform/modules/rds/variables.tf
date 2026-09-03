variable "environment" {
  type = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}

variable "vpc_id" {
  type = string
}

variable "private_subnet_ids" {
  type = list(string)
}

variable "allowed_security_group_ids" {
  description = "Security groups allowed tcp/5432 inbound (Api, Worker, MigrationRunner task SGs)."
  type        = list(string)
}

variable "engine_version" {
  description = "Confirmed available in sa-east-1 via `aws rds describe-db-engine-versions` (CP5.3B Decision Gate) - latest PostgreSQL 16.x minor at research time."
  type        = string
  default     = "16.15"
}

variable "instance_class" {
  type    = string
  default = "db.t4g.micro"
}

variable "allocated_storage_gb" {
  type    = number
  default = 20
}

variable "max_allocated_storage_gb" {
  type    = number
  default = 100
}

variable "backup_retention_days" {
  description = "Operational disaster-recovery retention (BackupRetentionIsOperationalDisasterRecovery=true) - distinct from any LGPD data-retention policy, never conflated."
  type        = number
  default     = 7
}

variable "database_name" {
  type    = string
  default = "ihostpro"
}

variable "app_role_name" {
  type    = string
  default = "ihostpro_app"
}

variable "migrator_role_name" {
  type    = string
  default = "ihostpro_migrator"
}

variable "app_secret_arn" {
  description = "ARN of the pre-existing (CP5.3A) empty ihostpro/<environment>/database/app secret container - this module writes the generated connection string into it via a write-only argument (never persisted in Terraform state)."
  type        = string
}

variable "migrator_secret_arn" {
  description = "ARN of the pre-existing (CP5.3A) empty ihostpro/<environment>/database/migrator secret container."
  type        = string
}
