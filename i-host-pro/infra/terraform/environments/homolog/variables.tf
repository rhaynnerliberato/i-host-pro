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
variable "image_tag" {
  description = "Immutable git-SHA tag of the images already pushed to ECR (Api/Worker/MigrationRunner/DatabaseBootstrap all share one tag per release)."
  type        = string
}
