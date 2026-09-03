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

# CP5.3D-A Decision Gate item 3/42: empty until BaseDomain is decided and a
# real ACM certificate exists - the alb module creates zero listeners while
# this is empty (never a fake certificate).
variable "alb_certificate_arn" {
  type    = string
  default = ""
}
