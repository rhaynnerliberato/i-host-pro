terraform {
  required_version = ">= 1.11"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    tls = {
      source  = "hashicorp/tls"
      version = "~> 4.0"
    }
  }

  # bucket/region intentionally omitted here (partial backend config) — pass
  # them via -backend-config at init time, using the bootstrap root's
  # state_bucket_name output. See ../../README.md.
  backend "s3" {
    key          = "global/terraform.tfstate"
    use_lockfile = true
    encrypt      = true
  }
}

provider "aws" {
  region = var.region
}
