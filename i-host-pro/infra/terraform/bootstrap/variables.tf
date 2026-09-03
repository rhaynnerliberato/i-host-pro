variable "region" {
  description = "AWS region for the Terraform state bucket."
  type        = string
  default     = "sa-east-1"
}

variable "state_bucket_prefix" {
  description = "Prefix for the Terraform remote state bucket name. A random suffix is appended by this root module (via random_id, stored in this root's own local state) to satisfy S3's globally-unique-bucket-name requirement without asking for a manually chosen name or using the AWS Account ID as a suffix."
  type        = string
  default     = "ihostpro-terraform-state"
}

variable "project" {
  description = "Project tag applied to all resources."
  type        = string
  default     = "iHostPro"
}
