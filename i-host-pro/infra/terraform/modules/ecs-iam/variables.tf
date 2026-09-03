variable "environment" {
  type = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}

variable "execution_role_secret_arns" {
  description = "Secret ARNs the ECS agent (execution role) may inject as task definition environment variables at container start - the traditional IConfiguration-consumed secrets (DB/RabbitMQ/Redis/JWT), not the ones resolved by the new AWS SDK-backed credential providers at runtime."
  type        = list(string)
  default     = []
}

variable "api_task_secret_arns" {
  description = "Secret ARNs the Api task role may GetSecretValue at runtime - only what SecretsManagerWhatsAppWebhookCredentialProvider/SecretsManagerWhatsAppCredentialProvider actually call (Anthropic is Worker-only, never Api - AIAgentModuleExtensions doc comment confirms IHostPro.Api never references the AIAgent module)."
  type        = list(string)
  default     = []
}

variable "worker_task_secret_arns" {
  description = "Secret ARNs the Worker task role may GetSecretValue at runtime - Anthropic plus whatever per-tenant WhatsApp secrets an async job might need to resolve."
  type        = list(string)
  default     = []
}

variable "tenant_secret_arn_pattern" {
  description = "Wildcard ARN pattern for the per-tenant WhatsApp secret namespace (e.g. arn:aws:secretsmanager:*:*:secret:ihostpro/homolog/tenants/*) - granted to both Api and Worker task roles, since either can trigger an outbound WhatsApp send. Left empty (no statement added) if not supplied."
  type        = string
  default     = ""
}

variable "database_bootstrap_task_secret_arns" {
  description = "Secret ARNs the DatabaseBootstrap task role may GetSecretValue at runtime - EXACTLY the RDS master secret plus database/app and database/migrator, never RabbitMQ/Redis/Anthropic/Meta/JWT/Grafana (CP5.3C corrective Decision Gate item 5)."
  type        = list(string)
  default     = []
}

variable "rabbitmq_secret_arn" {
  description = "The rabbitmq secret ARN the credential rotation task role may Get/PutSecretValue - nothing else. Left empty (no statement added) if not supplied."
  type        = string
  default     = ""
}
