variable "environment" {
  type = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}

variable "repository_names" {
  description = "Short names for the ECR repositories to create — prefixed with ihostpro-<environment>-."
  type        = list(string)
  default     = ["api", "worker", "migrationrunner", "database-bootstrap", "rabbitmq-credential-rotation"]
}
