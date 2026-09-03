# CP5.3C corrective Decision Gate item 20: ECS cluster only - no Api/Worker
# services, no ALB, no target groups. One shared cluster for every task/
# service Homolog will ever need (the natural minimal design - nothing about
# an ECS cluster itself is per-workload).
resource "aws_ecs_cluster" "this" {
  # CP5.3C corrective Decision Gate item 8 audit: the pre-existing (CP5.2)
  # ihostpro-<env>-deploy IAM role's EcsServiceUpdate statement already
  # scopes its resource to service/ihostpro-<env>-cluster/* - naming the
  # cluster to match avoids touching that already-applied policy at all,
  # rather than the other way around.
  name = "ihostpro-${var.environment}-cluster"

  setting {
    name  = "containerInsights"
    value = "disabled" # No concrete requirement drives this cost/complexity yet - same reasoning already applied to Performance Insights/Enhanced Monitoring on RDS.
  }

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_cloudwatch_log_group" "database_bootstrap" {
  name              = "/ecs/ihostpro-${var.environment}-database-bootstrap"
  retention_in_days = var.log_retention_days

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_cloudwatch_log_group" "migrationrunner" {
  name              = "/ecs/ihostpro-${var.environment}-migrationrunner"
  retention_in_days = var.log_retention_days

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# --- DatabaseBootstrap one-off task (CP5.3C corrective Decision Gate item
# 21). Reads its 3 secret ARNs itself via the AWS SDK (task role), so they
# are passed as plain, non-secret environment variables - the ARN strings
# themselves carry no credential material, only Secrets Manager resource
# identifiers (already Terraform outputs). ---
resource "aws_ecs_task_definition" "database_bootstrap" {
  family                   = "ihostpro-${var.environment}-database-bootstrap"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = var.execution_role_arn
  task_role_arn            = var.database_bootstrap_task_role_arn

  container_definitions = jsonencode([
    {
      name      = "database-bootstrap"
      image     = var.database_bootstrap_image
      essential = true
      environment = [
        { name = "DatabaseBootstrap__RdsMasterSecretArn", value = var.rds_master_user_secret_arn },
        { name = "DatabaseBootstrap__AppSecretArn", value = var.database_app_secret_arn },
        { name = "DatabaseBootstrap__MigratorSecretArn", value = var.database_migrator_secret_arn },
        { name = "DatabaseBootstrap__RdsHost", value = var.rds_host },
        { name = "DatabaseBootstrap__RdsPort", value = tostring(var.rds_port) },
        { name = "DatabaseBootstrap__RdsDatabaseName", value = var.rds_database_name },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.database_bootstrap.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "database-bootstrap"
        }
      }
    }
  ])

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# --- MigrationRunner one-off task (CP5.3C corrective Decision Gate item
# 21). Connection strings and RabbitMQ config are injected as ECS `secrets`
# (execution-role-resolved), matching the existing IConfiguration-consumed
# shape exactly - zero application code change. All 12 ConnectionStrings:
# <Context> keys point at the SAME database/migrator secret ARN (one
# connection string reused across every Bounded Context, per the RDS
# module's design). RabbitMq:* keys are extracted from individual JSON
# fields of the rabbitmq secret via ECS's native `<arn>:<json-key>::`
# syntax - no custom code needed to unpack it. ---
locals {
  migrationrunner_connection_string_contexts = [
    "Identity", "PropertyManagement", "Reservations", "Configuration",
    "Housekeeping", "Dashboard", "Communication", "ExternalIntegrations",
    "GuestOperations", "Payments", "AIAgent", "Platform",
  ]

  migrationrunner_connection_string_secrets = [
    for context in local.migrationrunner_connection_string_contexts : {
      name      = "ConnectionStrings__${context}"
      valueFrom = var.database_migrator_secret_arn
    }
  ]

  migrationrunner_rabbitmq_secrets = [
    { name = "RabbitMq__Host", valueFrom = "${var.rabbitmq_secret_arn}:host::" },
    { name = "RabbitMq__Port", valueFrom = "${var.rabbitmq_secret_arn}:port::" },
    { name = "RabbitMq__VirtualHost", valueFrom = "${var.rabbitmq_secret_arn}:virtualHost::" },
    { name = "RabbitMq__Username", valueFrom = "${var.rabbitmq_secret_arn}:username::" },
    { name = "RabbitMq__Password", valueFrom = "${var.rabbitmq_secret_arn}:password::" },
    { name = "RabbitMq__UseTls", valueFrom = "${var.rabbitmq_secret_arn}:useTls::" },
  ]
}

resource "aws_ecs_task_definition" "migrationrunner" {
  family                   = "ihostpro-${var.environment}-migrationrunner"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = var.execution_role_arn
  task_role_arn            = var.migrationrunner_task_role_arn

  container_definitions = jsonencode([
    {
      name      = "migrationrunner"
      image     = var.migrationrunner_image
      essential = true
      secrets   = concat(local.migrationrunner_connection_string_secrets, local.migrationrunner_rabbitmq_secrets)
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.migrationrunner.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "migrationrunner"
        }
      }
    }
  ])

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}
