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

variable "auth_token" {
  description = "AUTH token value. CP5.3B Decision Gate finding: aws_elasticache_replication_group.auth_token has NO write-only variant in the installed hashicorp/aws 5.100.0 provider (confirmed via `terraform providers schema -json`) - setting this WILL persist the plaintext value in Terraform state. Left null (AUTH disabled) until an explicit decision is made among the alternatives in the CP5.3B report. Never pass a real value here without that decision."
  type        = string
  default     = null
  sensitive   = true
}
