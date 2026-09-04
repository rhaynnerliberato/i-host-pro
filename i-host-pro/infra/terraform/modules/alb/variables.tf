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

variable "public_subnet_ids" {
  type = list(string)
}

variable "security_group_id" {
  type = string
}

# CP5.3D-B: this module is only ever instantiated once BaseDomain/ACM are
# decided (gated by the caller's enable_runtime_edge, not by this variable
# any more) - a real certificate ARN is always expected here, usually the
# not-yet-known output of a Terraform-managed aws_acm_certificate_validation
# in the same apply. No default/empty-string sentinel: making the listeners
# themselves conditional on "is this empty" broke plan-time evaluation once
# the value became a computed (not literal) string - see CP5.3D-B report.
variable "certificate_arn" {
  description = "ACM certificate ARN for the HTTPS listener - required, no fake/placeholder certificate is ever created."
  type        = string
}

variable "access_logs_enabled" {
  type    = bool
  default = true
}

variable "access_logs_bucket" {
  description = "S3 bucket name for ALB access logs - required only when access_logs_enabled is true."
  type        = string
  default     = ""
}
