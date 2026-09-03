variable "github_org" {
  description = "GitHub organization or user that owns the repository."
  type        = string
}

variable "github_repo" {
  description = "GitHub repository name, without the org prefix."
  type        = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}

variable "environments" {
  description = "Map of Terraform-side environment key => GitHub Environment name used in the OIDC trust condition's :environment: claim. One IAM role is created per entry."
  type        = map(string)
  default = {
    homolog    = "homolog"
    production = "production"
  }
}
