terraform {
  required_version = ">= 1.11"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }

  # Bootstrap is the one Terraform root that cannot use the S3 backend it
  # itself creates (chicken-and-egg) — local state only, run once. See
  # ../README.md for the full bootstrap sequence.
  backend "local" {
    path = "terraform.tfstate"
  }
}

provider "aws" {
  region = var.region
}
