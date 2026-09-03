# Account-wide resources that do not belong to any single environment's VPC
# (IAM/OIDC and Budgets are account/region-scoped, not VPC-scoped) — kept in
# one root so SINGLE_ACCOUNT_STRONG_LOGICAL_SEPARATION doesn't need a second
# AWS account just to hold them.

locals {
  # ProductionDeployRole is gated separately from HomologDeployRole — see
  # var.create_production_deploy_role's description. Homolog is always on.
  oidc_environments = merge(
    { homolog = "homolog" },
    var.create_production_deploy_role ? { production = "production" } : {}
  )
}

module "github_oidc" {
  source = "../../modules/github-oidc"

  github_org   = var.github_org
  github_repo  = var.github_repo
  environments = local.oidc_environments
}

module "budget" {
  source = "../../modules/budget"

  alert_email = var.budget_alert_email
}
