# Generated once and then kept stable by this root's own local state — never
# regenerated on subsequent applies (random_id only changes if its inputs
# change, which they never do here).
resource "random_id" "state_bucket_suffix" {
  byte_length = 4
}

resource "aws_s3_bucket" "terraform_state" {
  bucket = "${var.state_bucket_prefix}-${random_id.state_bucket_suffix.hex}"

  # Guards against an accidental `terraform destroy` run inside this
  # bootstrap root from ever deleting the state bucket every other root
  # depends on.
  lifecycle {
    prevent_destroy = true
  }

  tags = {
    Project   = var.project
    ManagedBy = "Terraform"
    Purpose   = "terraform-remote-state"
  }
}

resource "aws_s3_bucket_versioning" "terraform_state" {
  bucket = aws_s3_bucket.terraform_state.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "terraform_state" {
  bucket = aws_s3_bucket.terraform_state.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_public_access_block" "terraform_state" {
  bucket = aws_s3_bucket.terraform_state.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "terraform_state" {
  bucket = aws_s3_bucket.terraform_state.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}
