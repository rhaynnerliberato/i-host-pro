variable "region" {
  type    = string
  default = "sa-east-1"
}

variable "github_org" {
  description = "GitHub organization/user that owns the repository."
  type        = string
}

variable "github_repo" {
  description = "GitHub repository name, without the org prefix."
  type        = string
}

variable "budget_alert_email" {
  description = "Email to receive AWS Budget alert notifications. No default — DecisionRequired: must be supplied explicitly (see CP5.2 report, item 35)."
  type        = string
}

variable "create_production_deploy_role" {
  description = "Gate for ihostpro-production-deploy. Kept false until the GitHub \"production\" Environment has required-reviewer protection confirmed — the role's code exists and is parameterized, but is not part of the plan/apply while this is false."
  type        = bool
  default     = false
}
