# Account-wide resources that do not belong to any single environment's VPC
# (IAM/OIDC and Budgets are account/region-scoped, not VPC-scoped) — kept in
# one root so SINGLE_ACCOUNT_STRONG_LOGICAL_SEPARATION doesn't need a second
# AWS account just to hold them.

module "github_oidc" {
  source = "../../modules/github-oidc"

  github_org  = var.github_org
  github_repo = var.github_repo
}

module "budget" {
  source = "../../modules/budget"

  alert_email = var.budget_alert_email
}
