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
  description = "Security groups allowed tcp/6379 inbound (Api, Worker task SGs - never MigrationRunner, which has no Redis usage - CP5.3B item 23)."
  type        = list(string)
}

variable "engine_version" {
  description = "Confirmed available in sa-east-1 via `aws elasticache describe-cache-engine-versions --engine valkey` (CP5.3B Decision Gate)."
  type        = string
  default     = "8.1"
}

variable "node_type" {
  type    = string
  default = "cache.t4g.micro"
}

variable "redis_secret_arn" {
  description = "ARN of the pre-existing (CP5.3A) empty ihostpro/<environment>/redis secret container - this module writes the generated AUTH token/connection string into it via a write-only argument."
  type        = string
}
