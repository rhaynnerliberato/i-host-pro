# CP5.2 scope: network foundation + ECR. CP5.3A adds: IAM runtime roles +
# Secrets Manager secret containers (empty - no value populated by
# Terraform). RDS/ElastiCache/Amazon MQ/ECS services/ALB/CloudFront are
# still NOT created here — see ../../README.md for the full CP5 sequencing.

# CP5.3D-B item 6/7: create_alb_http_ingress references local.runtime_edge_enabled,
# declared further below - valid regardless of textual order (Terraform
# resolves the dependency graph, not file position).
module "network" {
  source = "../../modules/network"

  environment             = "homolog"
  create_alb_http_ingress = local.runtime_edge_enabled
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

  # CP5.3C RabbitMQ credential rotation subgate: EXACTLY the rabbitmq
  # secret, nothing else.
  rabbitmq_secret_arn = module.credentials.secret_arns["rabbitmq"]

  # CP5.3D-C corrective Decision Gate: EXACTLY database/app (read-only, the
  # same connection string Api/Worker use) plus the new admin-password
  # secret (read+write - this tool is the only thing that ever populates
  # it). Never database/migrator or the RDS master secret.
  tenant_provisioning_read_secret_arns          = [module.credentials.secret_arns["database/app"]]
  tenant_provisioning_admin_password_secret_arn = module.credentials.secret_arns["identity/bootstrap-admin-password"]
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
    # CP5.3D-C corrective Decision Gate: real, live-execution bug found and
    # fixed - this was omitted when the tool was first designed, causing a
    # real Npgsql connection timeout (RDS's own SG had no ingress rule for
    # this SG, so packets were silently dropped, not actively refused).
    module.network.tenant_provisioning_security_group_id,
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

  rabbitmq_secret_arn        = module.credentials.secret_arns["rabbitmq"]
  rotation_security_group_id = module.network.rabbitmq_rotation_security_group_id
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

  execution_role_arn                = module.ecs_iam.execution_role_arn
  database_bootstrap_task_role_arn  = module.ecs_iam.database_bootstrap_task_role_arn
  migrationrunner_task_role_arn     = module.ecs_iam.migrationrunner_task_role_arn
  rabbitmq_rotation_task_role_arn   = module.ecs_iam.rabbitmq_rotation_task_role_arn
  tenant_provisioning_task_role_arn = module.ecs_iam.tenant_provisioning_task_role_arn

  database_bootstrap_image  = "${module.ecr.repository_urls["database-bootstrap"]}:${var.database_bootstrap_image_tag}"
  migrationrunner_image     = "${module.ecr.repository_urls["migrationrunner"]}:${var.migrationrunner_image_tag}"
  rabbitmq_rotation_image   = "${module.ecr.repository_urls["rabbitmq-credential-rotation"]}:${var.rabbitmq_rotation_image_tag}"
  tenant_provisioning_image = "${module.ecr.repository_urls["tenant-provisioning"]}:${var.tenant_provisioning_image_tag}"

  database_bootstrap_security_group_id = module.network.database_bootstrap_security_group_id
  migrationrunner_security_group_id    = module.network.migrationrunner_security_group_id
  rabbitmq_rotation_security_group_id  = module.network.rabbitmq_rotation_security_group_id
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

  # CP5.3D-C corrective Decision Gate: the exact tenant/admin identity to
  # provision - real decisions, never invented here (see
  # environments/homolog/variables.tf for why these have no default).
  tenant_provisioning_admin_password_secret_arn = module.credentials.secret_arns["identity/bootstrap-admin-password"]
  tenant_provisioning_tenant_slug               = var.tenant_provisioning_tenant_slug
  tenant_provisioning_tenant_name               = var.tenant_provisioning_tenant_name
  tenant_provisioning_admin_email               = var.tenant_provisioning_admin_email
  tenant_provisioning_admin_full_name           = var.tenant_provisioning_admin_full_name
}

# CP5.3D-B Decision Gate item 37/38: the apply must be split into two
# subgates - B1 (this zone only) so the user can delegate nameservers at
# Registro.br before anything tries to DNS-validate against them; B2
# (everything below) blocked until that delegation has actually propagated.
# runtime_edge_enabled is deliberately ANDed with the zone flag, not just
# enable_runtime_edge alone - it must be structurally impossible for B2's
# modules to reference a route53 zone that doesn't exist (item 7: a
# declarative, reviewable gate, not a `-target` CLI trick).
locals {
  route53_zone_enabled = var.enable_route53_zone
  runtime_edge_enabled = var.enable_runtime_edge && var.enable_route53_zone
}

# CP5.3D-B: one hosted zone for the whole registered apex domain (item 4 -
# never a per-environment sub-zone). Registrar stays Registro.br - only DNS
# authority moves here. The ONLY manual step this leaves the user (item
# 5/6) is swapping Registro.br's current nameservers (a.auto.dns.br/
# b.auto.dns.br) for this zone's 4 real ones, once applied (= B1).
module "route53" {
  count = local.route53_zone_enabled ? 1 : 0

  source = "../../modules/route53"

  domain_name = var.base_domain
}

# CP5.3D-B item 9/10/16: fully Terraform-managed DNS-validated certificate -
# no manual CNAME creation. Gated on runtime_edge_enabled (= B2), never
# created before the zone's nameservers are actually live at the registrar
# (item 37/38) - the validation resource inside this module would otherwise
# hang waiting on DNS that isn't authoritative yet.
module "acm_certificate" {
  count = local.runtime_edge_enabled ? 1 : 0

  source = "../../modules/acm-certificate"

  domain_name = "api.homolog.${var.base_domain}"
  zone_id     = module.route53[0].zone_id
}

# CP5.3D-A Decision Gate final decisions (item 13): gated on
# runtime_edge_enabled (= B2) - while false, this module contributes ZERO
# resources to the plan (no ALB, no target group, no log bucket). CP5.3D-B:
# certificate_arn now sourced directly from the Terraform-managed ACM
# module instead of a manually-supplied ARN - no apply this checkpoint
# (TerraformApplyAuthorized=false for B2).
module "alb" {
  count = local.runtime_edge_enabled ? 1 : 0

  source = "../../modules/alb"

  environment       = "homolog"
  vpc_id            = module.network.vpc_id
  public_subnet_ids = module.network.public_subnet_ids
  security_group_id = module.network.alb_security_group_id
  certificate_arn   = module.acm_certificate[0].certificate_arn
}

# CP5.3D-A Decision Gate final decisions (item 13): same runtime_edge_enabled
# (= B2) gate as module.alb above (item 47's design is unchanged, only now
# actually absent from the plan while the flag is false) - Api/Worker ECS
# services reference module.alb[0], which only exists when this module also
# does, since both share the identical condition. DESIGN ONLY -
# TerraformApplyAuthorized=false for B2. Reuses the Api/Worker SGs already
# created in CP5.2 (modules/network) and the task roles already created in
# CP5.3A (modules/ecs-iam) - only the service/task-definition resources
# themselves are new.
module "ecs_services" {
  count = local.runtime_edge_enabled ? 1 : 0

  source = "../../modules/ecs-services"

  environment = "homolog"
  aws_region  = var.region
  cluster_arn = module.ecs.cluster_arn

  execution_role_arn   = module.ecs_iam.execution_role_arn
  api_task_role_arn    = module.ecs_iam.api_task_role_arn
  worker_task_role_arn = module.ecs_iam.worker_task_role_arn

  api_image_tag             = var.api_image_tag
  worker_image_tag          = var.worker_image_tag
  api_ecr_repository_url    = module.ecr.repository_urls["api"]
  worker_ecr_repository_url = module.ecr.repository_urls["worker"]

  api_security_group_id    = module.network.api_security_group_id
  worker_security_group_id = module.network.worker_security_group_id
  public_subnet_ids        = module.network.public_subnet_ids
  alb_target_group_arn     = module.alb[0].target_group_arn

  database_app_secret_arn              = module.credentials.secret_arns["database/app"]
  rabbitmq_secret_arn                  = module.credentials.secret_arns["rabbitmq"]
  redis_secret_arn                     = module.credentials.secret_arns["redis"]
  jwt_signing_key_secret_arn           = module.credentials.secret_arns["jwt/signing-key"]
  anthropic_secret_arn                 = module.credentials.secret_arns["anthropic"]
  meta_webhook_app_secret_arn          = module.credentials.secret_arns["meta/webhook/app-secret"]
  meta_webhook_verify_token_secret_arn = module.credentials.secret_arns["meta/webhook/verify-token"]
}

# CP5.3D-B item 36: the public DNS name for the Api - an alias record (no
# separate hosted-zone charge, resolves directly to the ALB, tracks its IPs
# automatically). Same runtime_edge_enabled (= B2) gate as the rest of this
# section.
resource "aws_route53_record" "api_homolog" {
  count = local.runtime_edge_enabled ? 1 : 0

  zone_id = module.route53[0].zone_id
  name    = "api.homolog.${var.base_domain}"
  type    = "A"

  alias {
    name                   = module.alb[0].alb_dns_name
    zone_id                = module.alb[0].alb_zone_id
    evaluate_target_health = true
  }
}
