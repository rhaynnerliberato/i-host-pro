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

# CP5.3D-A Decision Gate final decisions (item 13): BaseDomain was
# USER_DECISION_PENDING at the time - an empty certificate ARN alone must
# not be the only thing standing between this plan and creating half of the
# runtime edge (ALB, target group, log bucket, Api/Worker services). This
# flag gates the module blocks themselves (main.tf), so while false the plan
# contains ZERO resources from modules "route53", "acm_certificate", "alb"
# and "ecs_services" - not just gated listeners inside an otherwise-created
# ALB. CP5.3D-B Decision Gate item 32: BaseDomain is now resolved, so the
# default flips to true - TerraformApplyAuthorized remains the separate,
# standing procedural gate that actually controls whether `terraform apply`
# runs (this flag only controls what's IN the plan).
variable "enable_runtime_edge" {
  description = "Explicit switch for the Route53/ACM/ALB and ECS services (Api/Worker) modules. True now that BaseDomain is resolved (CP5.3D-B) - apply is still gated separately."
  type        = bool
  default     = true
}
