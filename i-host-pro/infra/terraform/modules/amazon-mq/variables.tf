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
  description = "Security groups allowed tcp/5671 inbound (Api, Worker, MigrationRunner task SGs - MigrationRunner genuinely needs this, confirmed by DeclareExchange calls in tools/IHostPro.MigrationRunner/Program.cs)."
  type        = list(string)
}

variable "engine_version" {
  description = "Confirmed available in sa-east-1 via `aws mq describe-broker-engine-types --engine-type RABBITMQ` (CP5.3B Decision Gate): only 4.2 and 3.13 exist. 3.13 selected - matches the rabbitmq:3-management-alpine image already used in local dev, minimizing drift on the first cloud cutover."
  type        = string
  default     = "3.13"
}

variable "instance_type" {
  type    = string
  default = "mq.t3.micro"
}

variable "broker_username" {
  type    = string
  default = "ihostpro"
}

variable "broker_password" {
  description = "CP5.3B Decision Gate finding: aws_mq_broker's nested user block has NO write-only password variant in the installed hashicorp/aws 5.100.0 provider, and the attribute is REQUIRED (unlike ElastiCache's optional auth_token, this resource cannot even be created without a value). This module is written but deliberately NOT wired into environments/homolog/main.tf yet - creating it requires resolving the same state-exposure decision as Valkey's auth_token first. No default - must never be silently invented."
  type        = string
  sensitive   = true
}
