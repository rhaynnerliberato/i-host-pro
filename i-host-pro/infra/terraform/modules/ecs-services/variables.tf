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

variable "cluster_arn" {
  type = string
}

variable "execution_role_arn" {
  type = string
}

variable "api_task_role_arn" {
  type = string
}

variable "worker_task_role_arn" {
  type = string
}

# Same IMMUTABLE-tag reasoning as modules/ecs - no default, and deliberately
# separate per service (CP5.3C already established the "don't force an
# unrelated rebuild just to share one tag variable" precedent).
variable "api_image_tag" {
  type = string
}

variable "worker_image_tag" {
  type = string
}

variable "api_ecr_repository_url" {
  type = string
}

variable "worker_ecr_repository_url" {
  type = string
}

variable "api_security_group_id" {
  type = string
}

variable "worker_security_group_id" {
  type = string
}

variable "public_subnet_ids" {
  type = list(string)
}

variable "alb_target_group_arn" {
  description = "Api service registers with this target group. No default - a service without one would be pointless."
  type        = string
}

# --- Secret ARNs (NON_SECRET_CONFIG - resource identifiers, never values) ---
variable "database_app_secret_arn" {
  type = string
}

variable "rabbitmq_secret_arn" {
  type = string
}

variable "redis_secret_arn" {
  type = string
}

variable "jwt_signing_key_secret_arn" {
  description = "Api only - Worker never binds JwtSigningKeyOptions (confirmed: AddIdentityJwtIssuance is called only from IHostPro.Api's Program.cs)."
  type        = string
}

variable "anthropic_secret_arn" {
  description = "Worker only (AIAgent module) - passed as a plain, non-secret ARN reference; the credential provider resolves the actual value itself via the AWS SDK."
  type        = string
}

variable "meta_webhook_app_secret_arn" {
  description = "Api only (ExternalIntegrations module) - plain ARN reference, same resolution pattern as Anthropic."
  type        = string
}

variable "meta_webhook_verify_token_secret_arn" {
  type = string
}

# CP5.3D-A Decision Gate item 34: pilot baseline, autoscaling explicitly
# disabled - not exposed as a tunable variable, since var.desired_count=2
# would silently contradict PilotRuntimeMode=SINGLE_API_SINGLE_WORKER and
# HorizontalScaleOutVerified=false without a real re-verification.
locals {
  desired_count = 1
}

variable "log_retention_days" {
  type    = number
  default = 30
}
