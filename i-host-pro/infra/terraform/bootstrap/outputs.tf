output "state_bucket_name" {
  description = "Name of the S3 bucket holding Terraform remote state. Pass this as -backend-config=\"bucket=<value>\" when running terraform init in every other environment root."
  value       = aws_s3_bucket.terraform_state.id
}

output "state_bucket_region" {
  value = var.region
}
