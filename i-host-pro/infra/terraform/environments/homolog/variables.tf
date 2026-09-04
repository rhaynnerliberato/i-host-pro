variable "region" {
  type    = string
  default = "sa-east-1"
}

# CP5.3C corrective Decision Gate item 19: ECR repos are IMMUTABLE (CP5.2) -
# "latest" is not a usable tag (a second push to it would be rejected), so
# every task definition needs a real, already-pushed git-SHA tag. No
# default on purpose - a task definition built from a placeholder tag would
# reference an image that doesn't exist, which is worse than an explicit
# "you must supply this" plan-time error.
#
# Split per one-off task (runtime-proof correction) rather than one shared
# tag - the DatabaseBootstrap fix needed a rebuild+republish under a new SHA
# while MigrationRunner's image was genuinely unaffected and must NOT be
# forced through an unnecessary rebuild just to share a single variable.
variable "database_bootstrap_image_tag" {
  description = "Immutable git-SHA tag of the database-bootstrap image already pushed to ECR."
  type        = string
}

variable "migrationrunner_image_tag" {
  description = "Immutable git-SHA tag of the migrationrunner image already pushed to ECR."
  type        = string
}

variable "rabbitmq_rotation_image_tag" {
  description = "Immutable git-SHA tag of the rabbitmq-credential-rotation image already pushed to ECR."
  type        = string
}

variable "tenant_provisioning_image_tag" {
  description = "Immutable git-SHA tag of the tenant-provisioning image already pushed to ECR."
  type        = string
}

# CP5.3D-A Decision Gate item 33: Api/Worker source code is causally
# unchanged since 4c3f6c5 - no rebuild for aesthetics. No default: still
# requires an explicit, deliberate value at plan/apply time.
variable "api_image_tag" {
  description = "Immutable git-SHA tag of the api image already pushed to ECR."
  type        = string
}

variable "worker_image_tag" {
  description = "Immutable git-SHA tag of the worker image already pushed to ECR."
  type        = string
}

# CP5.3D-B Decision Gate: BaseDomain is now resolved - the user registered
# and controls this domain at Registro.br (WHOIS-confirmed, status
# "Publicado"). Real default now that this is a ratified decision, not a
# placeholder - the ACM certificate/DNS alias below are derived from this,
# never a separate hardcoded literal.
variable "base_domain" {
  description = "The registered apex domain whose DNS authority is delegated to this environment's Route53 zone."
  type        = string
  default     = "ihostpro.com.br"
}

# CP5.3D-B Decision Gate item 1/37/38 (subgate split, approved): the apply
# is split into B1 (route53 hosted zone only) and B2 (everything that
# depends on the zone's nameservers actually being live at the registrar -
# ACM validation, ALB, ECS services, DNS alias). This flag gates B1 alone.
variable "enable_route53_zone" {
  description = "Switch for the Route53 hosted zone module (CP5.3D-B1). True - B1 is authorized."
  type        = bool
  default     = true
}

# CP5.3D-A Decision Gate final decisions (item 13): BaseDomain was
# USER_DECISION_PENDING at the time - an empty certificate ARN alone must
# not be the only thing standing between this plan and creating half of the
# runtime edge (ALB, target group, log bucket, Api/Worker services). CP5.3D-B
# item 15 held this false until nameserver delegation to Route53 had
# propagated (creating the ACM certificate before then would hang on
# aws_acm_certificate_validation waiting for DNS that wasn't authoritative
# yet). CP5.3D-B2 kickoff (2026-09-04): delegation confirmed propagated -
# authoritative .br, Google DNS and Cloudflare all independently return the
# 4 real Route53 nameservers - so the default flips to true. This only
# controls what's IN the plan; TerraformApplyAuthorized remains the
# separate, standing procedural gate that actually controls whether
# `terraform apply` runs.
variable "enable_runtime_edge" {
  description = "Explicit switch for the ACM/ALB/ECS services (Api/Worker) modules - CP5.3D-B2. True now that nameserver delegation to Route53 has propagated."
  type        = bool
  default     = true
}

# CP5.3D-C corrective Decision Gate: the real tenant/admin identity for
# IHostPro.TenantProvisioning to provision - genuine business decisions
# (item 10/11 of that gate: "não inventar formato de TenantId", "não
# hardcode email"), never invented or defaulted here. No default on any of
# these: an empty/placeholder value would silently provision a meaningless
# tenant, worse than an explicit plan-time error.
variable "tenant_provisioning_tenant_slug" {
  description = "TenantSlug for the Homolog tenant (lowercase, 3-63 chars, [a-z0-9-] only - TenantSlug.Create's own validation)."
  type        = string
}

variable "tenant_provisioning_tenant_name" {
  description = "Display name for the Homolog tenant."
  type        = string
}

variable "tenant_provisioning_admin_email" {
  description = "Email address for the initial Homolog admin user."
  type        = string
}

variable "tenant_provisioning_admin_full_name" {
  description = "Full name for the initial Homolog admin user."
  type        = string
}
