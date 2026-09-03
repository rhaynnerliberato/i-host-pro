variable "environment" {
  type = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}

variable "secret_names" {
  description = "Short names for the Secrets Manager secret CONTAINERS to create under ihostpro/<environment>/. Values are NOT set here (SecretResourceCreated != SecretValuePopulated, CP5.3A mandate item 23) - every secret is created empty; a real value is populated out of Terraform, by whoever holds it, once the consuming service actually needs it."
  type        = list(string)
  default = [
    "database/app",
    "database/migrator",
    "rabbitmq",
    "redis",
    "anthropic",
    "meta/webhook/app-secret",
    "meta/webhook/verify-token",
    "jwt/signing-key",
    "observability/otlp",
  ]
}
