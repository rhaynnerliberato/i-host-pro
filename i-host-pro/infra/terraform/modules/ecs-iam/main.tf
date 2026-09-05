data "aws_iam_policy_document" "ecs_tasks_trust" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# --- Execution role: used by the ECS agent itself (image pull, secret
# injection into environment variables, CloudWatch Logs) - never by
# application code directly. ---
resource "aws_iam_role" "execution" {
  name               = "ihostpro-${var.environment}-ecs-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

data "aws_iam_policy_document" "execution_permissions" {
  statement {
    sid       = "EcrAuth"
    effect    = "Allow"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"] # ecr:GetAuthorizationToken does not support resource-level restriction (real AWS IAM limitation).
  }

  statement {
    sid    = "EcrPull"
    effect = "Allow"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:GetDownloadUrlForLayer",
      "ecr:BatchGetImage",
    ]
    resources = ["arn:aws:ecr:*:*:repository/ihostpro-${var.environment}-*"]
  }

  statement {
    sid    = "Logs"
    effect = "Allow"
    actions = [
      "logs:CreateLogStream",
      "logs:PutLogEvents",
    ]
    resources = ["arn:aws:logs:*:*:log-group:/ecs/ihostpro-${var.environment}-*:*"]
  }

  dynamic "statement" {
    for_each = length(var.execution_role_secret_arns) > 0 ? [1] : []
    content {
      sid       = "SecretInjection"
      effect    = "Allow"
      actions   = ["secretsmanager:GetSecretValue"]
      resources = var.execution_role_secret_arns
    }
  }
}

resource "aws_iam_role_policy" "execution" {
  name   = "ihostpro-${var.environment}-ecs-execution-permissions"
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.execution_permissions.json
}

# --- Task roles: used by application code at runtime (never
# AdministratorAccess; each service gets only what it actually calls). ---
resource "aws_iam_role" "api_task" {
  name               = "ihostpro-${var.environment}-api-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "api"
    ManagedBy   = "Terraform"
  }
}

resource "aws_iam_role" "worker_task" {
  name               = "ihostpro-${var.environment}-worker-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "worker"
    ManagedBy   = "Terraform"
  }
}

resource "aws_iam_role" "migrationrunner_task" {
  name               = "ihostpro-${var.environment}-migrationrunner-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "migrationrunner"
    ManagedBy   = "Terraform"
  }
}
# migrationrunner_task has no attached policy: it consumes database/migrator
# and rabbitmq via IConfiguration (execution-role-injected environment
# variables), never an AWS SDK call of its own - confirmed by reading its
# actual runtime configuration, not assumed.

resource "aws_iam_role" "collector_task" {
  name               = "ihostpro-${var.environment}-collector-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "collector"
    ManagedBy   = "Terraform"
  }
}
# CP5.3E (Observability Architecture): collector_task has no attached
# policy - the official OpenTelemetry Collector image reads its own
# Grafana Cloud endpoint/credentials from plain environment variables
# (execution-role-injected from the observability/otlp secret, same
# mechanism as every other secret in this codebase), never an AWS SDK call
# of its own. Never copy Api/Worker's task-role policies here.

# CP5.3C corrective Decision Gate item 24: dedicated task role for the
# one-off Database Bootstrap task. Unlike Api/Worker/MigrationRunner, this
# tool calls the AWS SDK directly (GetSecretValueAsync) rather than
# consuming execution-role-injected environment variables - it needs the
# RDS master secret, which is deliberately never wired into the shared
# execution role's SecretInjection statement.
resource "aws_iam_role" "database_bootstrap_task" {
  name               = "ihostpro-${var.environment}-database-bootstrap-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "database-bootstrap"
    ManagedBy   = "Terraform"
  }
}

data "aws_iam_policy_document" "database_bootstrap_task_permissions" {
  dynamic "statement" {
    for_each = length(var.database_bootstrap_task_secret_arns) > 0 ? [1] : []
    content {
      sid       = "SecretsManagerBootstrapReads"
      effect    = "Allow"
      actions   = ["secretsmanager:GetSecretValue"]
      resources = var.database_bootstrap_task_secret_arns
    }
  }
}

resource "aws_iam_role_policy" "database_bootstrap_task" {
  count  = length(var.database_bootstrap_task_secret_arns) > 0 ? 1 : 0
  name   = "ihostpro-${var.environment}-database-bootstrap-task-permissions"
  role   = aws_iam_role.database_bootstrap_task.id
  policy = data.aws_iam_policy_document.database_bootstrap_task_permissions.json
}

# CP5.3C RabbitMQ credential rotation subgate: dedicated task role, scoped
# to GetSecretValue + PutSecretValue on EXACTLY the rabbitmq secret ARN -
# nothing else. Unlike the DatabaseBootstrap task (read-only), this one
# must also write the rotated credential back.
resource "aws_iam_role" "rabbitmq_rotation_task" {
  name               = "ihostpro-${var.environment}-rabbitmq-rotation-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "rabbitmq-rotation"
    ManagedBy   = "Terraform"
  }
}

data "aws_iam_policy_document" "rabbitmq_rotation_task_permissions" {
  dynamic "statement" {
    for_each = var.rabbitmq_secret_arn != "" ? [1] : []
    content {
      sid       = "SecretsManagerRabbitMqReadWrite"
      effect    = "Allow"
      actions   = ["secretsmanager:GetSecretValue", "secretsmanager:PutSecretValue"]
      resources = [var.rabbitmq_secret_arn]
    }
  }
}

resource "aws_iam_role_policy" "rabbitmq_rotation_task" {
  count  = var.rabbitmq_secret_arn != "" ? 1 : 0
  name   = "ihostpro-${var.environment}-rabbitmq-rotation-task-permissions"
  role   = aws_iam_role.rabbitmq_rotation_task.id
  policy = data.aws_iam_policy_document.rabbitmq_rotation_task_permissions.json
}

# CP5.3D-C corrective Decision Gate: dedicated task role for the one-off
# Tenant/Admin provisioning task. Like DatabaseBootstrap/RabbitMqRotation,
# calls the AWS SDK directly - reads the database/app connection string
# (the same secret Api/Worker use, read-only) and both reads and writes the
# NEW admin-password secret (read-only would be pointless: this tool is the
# only thing that ever populates it).
resource "aws_iam_role" "tenant_provisioning_task" {
  name               = "ihostpro-${var.environment}-tenant-provisioning-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "tenant-provisioning"
    ManagedBy   = "Terraform"
  }
}

data "aws_iam_policy_document" "tenant_provisioning_task_permissions" {
  dynamic "statement" {
    for_each = length(var.tenant_provisioning_read_secret_arns) > 0 ? [1] : []
    content {
      sid       = "SecretsManagerAppConnectionRead"
      effect    = "Allow"
      actions   = ["secretsmanager:GetSecretValue"]
      resources = var.tenant_provisioning_read_secret_arns
    }
  }

  dynamic "statement" {
    for_each = var.tenant_provisioning_admin_password_secret_arn != "" ? [1] : []
    content {
      sid       = "SecretsManagerAdminPasswordReadWrite"
      effect    = "Allow"
      actions   = ["secretsmanager:GetSecretValue", "secretsmanager:PutSecretValue"]
      resources = [var.tenant_provisioning_admin_password_secret_arn]
    }
  }
}

resource "aws_iam_role_policy" "tenant_provisioning_task" {
  count  = length(var.tenant_provisioning_read_secret_arns) > 0 || var.tenant_provisioning_admin_password_secret_arn != "" ? 1 : 0
  name   = "ihostpro-${var.environment}-tenant-provisioning-task-permissions"
  role   = aws_iam_role.tenant_provisioning_task.id
  policy = data.aws_iam_policy_document.tenant_provisioning_task_permissions.json
}

# CP5.3D-D corrective Decision Gate: dedicated task role for the one-off
# Homolog test-fixture provisioning task (HomologScenarioProvisioning=
# TEST_FIXTURE_ONLY). Read-only - EXACTLY the database/app connection
# string, same secret Api/Worker/TenantProvisioning use - never
# database/migrator, never RDS master, never any other secret.
resource "aws_iam_role" "homolog_scenario_provisioning_task" {
  name               = "ihostpro-${var.environment}-homolog-scenario-provisioning-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = {
    Project     = var.project
    Environment = var.environment
    Service     = "homolog-scenario-provisioning"
    ManagedBy   = "Terraform"
  }
}

data "aws_iam_policy_document" "homolog_scenario_provisioning_task_permissions" {
  dynamic "statement" {
    for_each = length(var.homolog_scenario_provisioning_read_secret_arns) > 0 ? [1] : []
    content {
      sid       = "SecretsManagerAppConnectionRead"
      effect    = "Allow"
      actions   = ["secretsmanager:GetSecretValue"]
      resources = var.homolog_scenario_provisioning_read_secret_arns
    }
  }
}

resource "aws_iam_role_policy" "homolog_scenario_provisioning_task" {
  count  = length(var.homolog_scenario_provisioning_read_secret_arns) > 0 ? 1 : 0
  name   = "ihostpro-${var.environment}-homolog-scenario-provisioning-task-permissions"
  role   = aws_iam_role.homolog_scenario_provisioning_task.id
  policy = data.aws_iam_policy_document.homolog_scenario_provisioning_task_permissions.json
}

locals {
  # CP5.3D-A corrective audit: the tenant WhatsApp wildcard was originally
  # granted to BOTH task roles (CP5.3A) on the assumption that an outbound
  # send could originate from either host. Re-auditing the real DI
  # composition (IHostPro.Worker.csproj's own comment: only
  # AddExternalIntegrationsPixProvider is called from Worker - never the
  # full AddExternalIntegrationsModule that registers the WhatsApp
  # messaging/tenant-credential providers) confirms Worker never resolves
  # IWhatsAppCredentialProvider at all - the grant was over-privileged.
  # Api keeps it (ExternalIntegrations lives there); Worker no longer does.
  api_secret_arns    = concat(var.api_task_secret_arns, var.tenant_secret_arn_pattern != "" ? [var.tenant_secret_arn_pattern] : [])
  worker_secret_arns = var.worker_task_secret_arns
}

data "aws_iam_policy_document" "api_task_permissions" {
  dynamic "statement" {
    for_each = length(local.api_secret_arns) > 0 ? [1] : []
    content {
      sid       = "SecretsManagerRuntimeReads"
      effect    = "Allow"
      actions   = ["secretsmanager:GetSecretValue"]
      resources = local.api_secret_arns
    }
  }
}

resource "aws_iam_role_policy" "api_task" {
  count  = length(local.api_secret_arns) > 0 ? 1 : 0
  name   = "ihostpro-${var.environment}-api-task-permissions"
  role   = aws_iam_role.api_task.id
  policy = data.aws_iam_policy_document.api_task_permissions.json
}

data "aws_iam_policy_document" "worker_task_permissions" {
  dynamic "statement" {
    for_each = length(local.worker_secret_arns) > 0 ? [1] : []
    content {
      sid       = "SecretsManagerRuntimeReads"
      effect    = "Allow"
      actions   = ["secretsmanager:GetSecretValue"]
      resources = local.worker_secret_arns
    }
  }
}

resource "aws_iam_role_policy" "worker_task" {
  count  = length(local.worker_secret_arns) > 0 ? 1 : 0
  name   = "ihostpro-${var.environment}-worker-task-permissions"
  role   = aws_iam_role.worker_task.id
  policy = data.aws_iam_policy_document.worker_task_permissions.json
}
