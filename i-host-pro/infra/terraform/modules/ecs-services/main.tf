# CP5.3D-A Decision Gate: Api and Worker as long-running ECS services
# (distinct from the one-off tasks in modules/ecs). DESIGN ONLY this
# checkpoint - TerraformApplyAuthorized=false.
#
# Connection strings and RabbitMQ config follow the exact same pattern
# already proven for MigrationRunner (modules/ecs) - all 12
# ConnectionStrings:<Context> keys point at the SAME database/app secret
# (the app role, never migrator/master), RabbitMq:* keys are extracted from
# the rabbitmq secret's individual JSON fields via ECS's native
# `<arn>:<json-key>::` syntax. Redis section names verified from the real
# RateLimitingOptions/SessionRevocationCacheOptions/PolicyCacheOptions
# SectionName constants - PolicyCache's exact leaf property name should be
# re-confirmed against PolicyCacheOptions.cs before this is ever applied
# (not done in this design pass).
locals {
  connection_string_contexts = [
    "Identity", "PropertyManagement", "Reservations", "Configuration",
    "Housekeeping", "Dashboard", "Communication", "ExternalIntegrations",
    "GuestOperations", "Payments", "AIAgent", "Platform",
  ]

  app_connection_string_secrets = [
    for context in local.connection_string_contexts : {
      name      = "ConnectionStrings__${context}"
      valueFrom = var.database_app_secret_arn
    }
  ]

  rabbitmq_secrets = [
    { name = "RabbitMq__Host", valueFrom = "${var.rabbitmq_secret_arn}:host::" },
    { name = "RabbitMq__Port", valueFrom = "${var.rabbitmq_secret_arn}:port::" },
    { name = "RabbitMq__VirtualHost", valueFrom = "${var.rabbitmq_secret_arn}:virtualHost::" },
    { name = "RabbitMq__Username", valueFrom = "${var.rabbitmq_secret_arn}:username::" },
    { name = "RabbitMq__Password", valueFrom = "${var.rabbitmq_secret_arn}:password::" },
    { name = "RabbitMq__UseTls", valueFrom = "${var.rabbitmq_secret_arn}:useTls::" },
  ]

  # Same physical Redis/Valkey instance, 3 distinct config sections that
  # each independently bind their own ConnectionString (RateLimitingOptions,
  # SessionRevocationCacheOptions, PolicyCacheOptions - confirmed via their
  # real SectionName constants).
  redis_secrets = [
    { name = "RateLimiting__Redis__ConnectionString", valueFrom = var.redis_secret_arn },
    { name = "Identity__SessionRevocationCache__ConnectionString", valueFrom = var.redis_secret_arn },
    { name = "Configuration__PolicyCache__ConnectionString", valueFrom = var.redis_secret_arn },
  ]
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/ecs/ihostpro-${var.environment}-api"
  retention_in_days = var.log_retention_days

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_cloudwatch_log_group" "worker" {
  name              = "/ecs/ihostpro-${var.environment}-worker"
  retention_in_days = var.log_retention_days

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_ecs_task_definition" "api" {
  family                   = "ihostpro-${var.environment}-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = var.execution_role_arn
  task_role_arn            = var.api_task_role_arn

  container_definitions = jsonencode([
    {
      name      = "api"
      image     = "${var.api_ecr_repository_url}:${var.api_image_tag}"
      essential = true
      portMappings = [
        { containerPort = 8080, protocol = "tcp" }
      ]
      environment = [
        # ASPNETCORE_ENVIRONMENT design assumption (CP5.3D-A item 58): the
        # codebase only ever branches on IsDevelopment() vs "not Development"
        # - no distinct "Homolog" environment name is checked anywhere found
        # this session, so Homolog runs as "Production" from the app's own
        # point of view. Flagged for confirmation, not silently assumed as
        # final.
        { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
        { name = "AIAgent__Anthropic__Secrets__SecretsManagerSecretId", value = var.anthropic_secret_arn },
        { name = "ExternalIntegrations__WhatsApp__Webhook__Secrets__AppSecretSecretsManagerSecretId", value = var.meta_webhook_app_secret_arn },
        { name = "ExternalIntegrations__WhatsApp__Webhook__Secrets__VerifyTokenSecretsManagerSecretId", value = var.meta_webhook_verify_token_secret_arn },
      ]
      secrets = concat(
        local.app_connection_string_secrets,
        local.rabbitmq_secrets,
        local.redis_secrets,
        [{ name = "Identity__Jwt__SigningKey__PrivateKeyPem", valueFrom = var.jwt_signing_key_secret_arn }],
      )
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.api.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "api"
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

resource "aws_ecs_task_definition" "worker" {
  family                   = "ihostpro-${var.environment}-worker"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = var.execution_role_arn
  task_role_arn            = var.worker_task_role_arn

  container_definitions = jsonencode([
    {
      name      = "worker"
      image     = "${var.worker_ecr_repository_url}:${var.worker_image_tag}"
      essential = true
      environment = [
        { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
        { name = "AIAgent__Anthropic__Secrets__SecretsManagerSecretId", value = var.anthropic_secret_arn },
        # CP5.3D-D corrective Decision Gate: AIAgentModuleExtensions selects
        # FakeModelProvider whenever this key is absent/empty - never
        # implicitly derived from ASPNETCORE_ENVIRONMENT. Discovered live in
        # Homolog (real webhook proof reached the model step and got a Fake
        # response instead of a real Anthropic call) - this was the missing
        # switch, not a code bug.
        { name = "AIAgent__ModelProvider", value = "Anthropic" },
      ]
      # No jwt_signing_key here - AddIdentityJwtIssuance is never called
      # from IHostPro.Worker (confirmed in Program.cs). No Meta webhook
      # secret ARNs here either - AddExternalIntegrationsModule is
      # Api-only; Worker only calls AddExternalIntegrationsPixProvider
      # (confirmed via IHostPro.Worker.csproj's own comment), which needs
      # neither.
      secrets = concat(
        local.app_connection_string_secrets,
        local.rabbitmq_secrets,
        local.redis_secrets,
      )
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.worker.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "worker"
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

resource "aws_ecs_service" "api" {
  name            = "ihostpro-${var.environment}-api"
  cluster         = var.cluster_arn
  task_definition = aws_ecs_task_definition.api.arn
  desired_count   = local.desired_count
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = var.public_subnet_ids
    security_groups  = [var.api_security_group_id]
    assign_public_ip = true # No NAT Gateway (established pilot baseline) - needs a public IP to reach ECR/CloudWatch/Secrets Manager
  }

  load_balancer {
    target_group_arn = var.alb_target_group_arn
    container_name   = "api"
    container_port   = 8080
  }

  # Pilot baseline (desired_count=1): allows exactly one replacement task
  # to come up before the old one is stopped, so a deploy is never a hard
  # 0-task gap - without needing >1 steady-state task.
  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_ecs_service" "worker" {
  name            = "ihostpro-${var.environment}-worker"
  cluster         = var.cluster_arn
  task_definition = aws_ecs_task_definition.worker.arn
  desired_count   = local.desired_count
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = var.public_subnet_ids
    security_groups  = [var.worker_security_group_id]
    assign_public_ip = true
  }

  # No load_balancer block - Worker receives no inbound traffic (ALB only
  # targets Api). Health is the Dockerfile's own baked-in HEALTHCHECK
  # (Worker.Dockerfile), which ECS/Fargate observes natively - no separate
  # ECS-level health check config needed.

  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}
