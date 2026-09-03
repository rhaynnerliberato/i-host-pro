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

# CP5.3D-A Decision Gate item 42: no fake certificate is ever created here.
# Empty (the default) means BaseDomain/ACM are not yet decided - the module
# still creates the ALB and target group (so Api's real proof work isn't
# blocked on the domain decision alone), but creates ZERO listeners until a
# real certificate ARN is supplied. An ALB with no listeners is valid but
# non-functional - acceptable for a DESIGN_ONLY gate that explicitly must
# not apply anything yet.
variable "certificate_arn" {
  description = "ACM certificate ARN for the HTTPS listener - empty until BaseDomain is decided and a real certificate exists. No listener (HTTP or HTTPS) is created while this is empty."
  type        = string
  default     = ""
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
