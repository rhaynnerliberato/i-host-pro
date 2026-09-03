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

  # CP5.3C corrective Decision Gate item 5/24: EXACTLY these 3 - the RDS
  # master credential plus the two role connection strings it bootstraps.
  # Never RabbitMQ/Redis/Anthropic/Meta/JWT/Grafana.
  database_bootstrap_task_secret_arns = [
    module.rds.master_user_secret_arn,
    module.credentials.secret_arns["database/app"],
    module.credentials.secret_arns["database/migrator"],
  ]
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
    module.network.database_bootstrap_security_group_id,
  ]

  # Homolog-only exception (CP5.3B corrective Decision Gate item 1): the AWS
  # Free Plan rejected the module's own default of 7 with a real
  # FreeTierRestrictionError. This override is scoped to this environment
  # file only - the module default stays at 7, the Production minimum, so a
  # future Production environment never silently inherits this 1-day value.
  backup_retention_days = 1

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

  redis_secret_arn = module.credentials.secret_arns["redis"]
}

# ACCEPTED_PILOT_SECURITY_EXCEPTION (CP5.3B revised Decision Gate): the
# broker's bootstrap user password has no write-only path in the installed
# provider and is required at creation time - see modules/amazon-mq/main.tf
# for the full reasoning and the mandatory CP5.3C rotation plan.
module "amazon_mq" {
  source = "../../modules/amazon-mq"

  environment        = "homolog"
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids
  allowed_security_group_ids = [
    module.network.api_security_group_id,
    module.network.worker_security_group_id,
    module.network.migrationrunner_security_group_id,
  ]

  rabbitmq_secret_arn = module.credentials.secret_arns["rabbitmq"]
}

# CP5.3C corrective Decision Gate items 20-21: ECS cluster + the two one-off
# task definitions (DatabaseBootstrap, MigrationRunner). No Api/Worker ECS
# services, no ALB - those remain out of scope. Neither one-off task is run
# by this apply - task definitions only describe how `aws ecs run-task`
# would launch them, under a SEPARATE, still-not-granted execution
# authorization (DatabaseBootstrapExecutionAuthorized=false,
# MigrationExecutionAuthorized=false).
module "ecs" {
  source = "../../modules/ecs"

  environment = "homolog"
  aws_region  = var.region

  execution_role_arn               = module.ecs_iam.execution_role_arn
  database_bootstrap_task_role_arn = module.ecs_iam.database_bootstrap_task_role_arn
  migrationrunner_task_role_arn    = module.ecs_iam.migrationrunner_task_role_arn

  database_bootstrap_image = "${module.ecr.repository_urls["database-bootstrap"]}:${var.image_tag}"
  migrationrunner_image    = "${module.ecr.repository_urls["migrationrunner"]}:${var.image_tag}"

  database_bootstrap_security_group_id = module.network.database_bootstrap_security_group_id
  migrationrunner_security_group_id    = module.network.migrationrunner_security_group_id
  public_subnet_ids                    = module.network.public_subnet_ids

  rds_master_user_secret_arn   = module.rds.master_user_secret_arn
  database_app_secret_arn      = module.credentials.secret_arns["database/app"]
  database_migrator_secret_arn = module.credentials.secret_arns["database/migrator"]
  rabbitmq_secret_arn          = module.credentials.secret_arns["rabbitmq"]

  # NON_SECRET_CONFIG (CP5.3C runtime-proof correction) - already-known,
  # non-secret RDS endpoint identity, never inferred from the master secret.
  rds_host          = module.rds.endpoint
  rds_port          = module.rds.port
  rds_database_name = module.rds.database_name
}
