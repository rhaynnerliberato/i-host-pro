terraform {
  required_version = ">= 1.11"

  required_providers {
    aws = {
      # CP5.3B: upgraded from ~> 5.0 to ~> 6.62.0 specifically because
      # aws_elasticache_replication_group.auth_token_wo (write-only) only
      # exists from 6.x onward - confirmed via `terraform providers schema
      # -json` against the real downloaded 6.62.0 provider before touching
      # this file. Pinned to the exact minor known to work, never an
      # unbounded >= constraint.
      source  = "hashicorp/aws"
      version = "~> 6.62.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # bucket/region intentionally omitted here (partial backend config) — pass
  # them via -backend-config at init time, using the bootstrap root's
  # state_bucket_name output. See ../../README.md.
  backend "s3" {
    key          = "homolog/terraform.tfstate"
    use_lockfile = true
    encrypt      = true
  }
}

provider "aws" {
  region = var.region
}
