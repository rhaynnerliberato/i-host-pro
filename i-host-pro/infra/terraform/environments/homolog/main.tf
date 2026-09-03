# CP5.2 scope: network foundation + ECR. CP5.3A adds: IAM runtime roles +
# Secrets Manager secret containers (empty - no value populated by
# Terraform). RDS/ElastiCache/Amazon MQ/ECS services/ALB/CloudFront are
# still NOT created here — see ../../README.md for the full CP5 sequencing.

module "network" {
  source = "../../modules/network"

  environment = "homolog"
}

module "ecr" {
  source = "../../modules/ecr"

  environment = "homolog"
}

module "credentials" {
  source = "../../modules/credentials"

  environment = "homolog"
}

# Secret-to-role assignment reasoning (CP5.3A report item 21/22):
# - execution role: secrets injected as ECS task definition environment
#   variables at container start (the existing IConfiguration-consumed
#   shape - DB/RabbitMQ/Redis/JWT PEM), shared across all three task
#   families since it is only the ECS agent doing the injection, never
#   application code.
# - api_task: only what SecretsManagerWhatsAppWebhookCredentialProvider
#   calls at runtime (App Secret + Verify Token) - the webhook lives in
#   IHostPro.Api.
# - worker_task: only the Anthropic secret - AIAgentModuleExtensions'
#   own doc comment confirms IHostPro.Api never references the AIAgent
#   module, so Api never needs it.
# - Both api_task and worker_task also get the per-tenant WhatsApp secret
#   namespace via a scoped wildcard (an outbound send could originate from
#   either host) - never an unscoped secretsmanager:* wildcard.
# - migrationrunner_task: no policy at all - it consumes database/migrator
#   and rabbitmq via the execution role's environment-variable injection,
#   never an AWS SDK call of its own.
module "ecs_iam" {
  source = "../../modules/ecs-iam"

  environment = "homolog"

  execution_role_secret_arns = [
    module.credentials.secret_arns["database/app"],
    module.credentials.secret_arns["database/migrator"],
    module.credentials.secret_arns["rabbitmq"],
    module.credentials.secret_arns["redis"],
    module.credentials.secret_arns["jwt/signing-key"],
  ]

  api_task_secret_arns = [
    module.credentials.secret_arns["meta/webhook/app-secret"],
    module.credentials.secret_arns["meta/webhook/verify-token"],
  ]

  worker_task_secret_arns = [
    module.credentials.secret_arns["anthropic"],
  ]

  # Per-tenant WhatsApp secrets are created dynamically (one per tenant, at
  # WhatsApp-configuration time), never enumerable as fixed ARNs up front -
  # scoped to our own namespace only, never a bare secretsmanager:* wildcard.
  tenant_secret_arn_pattern = "arn:aws:secretsmanager:*:*:secret:ihostpro/homolog/tenants/*"
}

# CP5.3B: RDS PostgreSQL + ElastiCache Valkey. Amazon MQ's module exists
# (../../modules/amazon-mq) but is deliberately NOT wired in here yet - its
# broker user password has no write-only alternative in the installed
# provider and is a required argument (unlike Valkey's optional auth_token),
# so creating it would force a Terraform-state-exposure decision that has
# not been made yet (see the CP5.3B report).
module "rds" {
  source = "../../modules/rds"

  environment        = "homolog"
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids
  allowed_security_group_ids = [
    module.network.api_security_group_id,
    module.network.worker_security_group_id,
    module.network.migrationrunner_security_group_id,
  ]

  app_secret_arn      = module.credentials.secret_arns["database/app"]
  migrator_secret_arn = module.credentials.secret_arns["database/migrator"]
}

module "valkey" {
  source = "../../modules/valkey"

  environment        = "homolog"
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids
  allowed_security_group_ids = [
    module.network.api_security_group_id,
    module.network.worker_security_group_id,
  ]

  # auth_token intentionally omitted (module default = null) - see CP5.3B
  # report's credential-generation matrix.
}
