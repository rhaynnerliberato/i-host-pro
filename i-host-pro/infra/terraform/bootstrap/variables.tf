variable "region" {
  description = "AWS region for the Terraform state bucket."
  type        = string
  default     = "sa-east-1"
}

variable "state_bucket_name" {
  description = "Globally unique S3 bucket name for Terraform remote state (S3 bucket names are unique across ALL AWS accounts, not just this one) — must be provided explicitly, never defaulted."
  type        = string
}

variable "project" {
  description = "Project tag applied to all resources."
  type        = string
  default     = "iHostPro"
}
