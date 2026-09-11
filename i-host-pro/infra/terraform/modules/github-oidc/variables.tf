variable "github_org" {
  description = "GitHub organization or user that owns the repository."
  type        = string
}

variable "github_repo" {
  description = "GitHub repository name, without the org prefix."
  type        = string
}

# GitHub's OIDC token `sub` claim for this repository is immutable-ID-based
# (confirmed against a real token captured via a temporary CI diagnostic step,
# 2026-09-11: "repo:rhaynnerliberato@50849676/i-host-pro@1312876131:environment:
# homolog" - not the legacy plain-name "repo:<org>/<repo>:environment:<name>"
# this trust policy was originally written against). Per GitHub's own docs,
# repositories created (or opted in, or renamed/transferred) after the
# 2026-07-15 rollout receive this format by default - the numeric IDs guard
# against a renamed/transferred repo or org inheriting another entity's trust.
# Cross-checked independently against the public, unauthenticated GitHub API
# (GET /repos/rhaynnerliberato/i-host-pro -> id=1312876131, owner.id=50849676)
# - exact match.
variable "github_owner_id" {
  description = "GitHub numeric owner (user/org) ID - the immutable OWNER-ID in the OIDC sub claim's \"OWNER@OWNER-ID\" segment."
  type        = string
}

variable "github_repo_id" {
  description = "GitHub numeric repository ID - the immutable REPO-ID in the OIDC sub claim's \"REPO@REPO-ID\" segment."
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
