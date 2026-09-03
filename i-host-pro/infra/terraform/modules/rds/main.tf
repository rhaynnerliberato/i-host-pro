resource "aws_db_subnet_group" "this" {
  name       = "ihostpro-${var.environment}-rds"
  subnet_ids = var.private_subnet_ids

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_security_group" "this" {
  name        = "ihostpro-${var.environment}-rds"
  description = "RDS PostgreSQL - inbound tcp/5432 only from Api/Worker/MigrationRunner task SGs, never a public CIDR."
  vpc_id      = var.vpc_id

  ingress {
    description     = "PostgreSQL from application tasks"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = var.allowed_security_group_ids
  }

  tags = {
    Name        = "ihostpro-${var.environment}-rds-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# rds.force_ssl requires a custom parameter group - the default group does
# not allow setting it (CP5.3B Decision Gate item 15).
resource "aws_db_parameter_group" "this" {
  name   = "ihostpro-${var.environment}-postgres16"
  family = "postgres16"

  parameter {
    name  = "rds.force_ssl"
    value = "1"
    # ApplyType=dynamic (confirmed via `aws rds describe-db-parameters`) -
    # the setting is genuinely active immediately, no reboot required
    # (ParameterApplyStatus=in-sync, verified against the real instance).
    # apply_method is declared as "pending-reboot" anyway, matching what the
    # AWS API always echoes back for this parameter regardless of what was
    # submitted - declaring "immediate" here produced a perpetual (non-
    # converging) diff on every subsequent plan, since the API's read value
    # never matches it. This is the provider/API's canonical representation
    # for this attribute, not a functional statement about when the value
    # takes effect (CP5.3B corrective Decision Gate, round 2).
    apply_method = "pending-reboot"
  }

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_db_instance" "this" {
  identifier     = "ihostpro-${var.environment}"
  engine         = "postgres"
  engine_version = var.engine_version
  instance_class = var.instance_class

  db_name  = var.database_name
  username = "ihostpro_master"

  # AWS-managed master credential (CP5.3B item 13) - never touches Terraform
  # state or plan output, rotated automatically by AWS every 7 days. This is
  # a bootstrap-only credential; Api/Worker/MigrationRunner never use it.
  manage_master_user_password = true

  allocated_storage     = var.allocated_storage_gb
  max_allocated_storage = var.max_allocated_storage_gb
  storage_type          = "gp3"
  storage_encrypted     = true

  db_subnet_group_name   = aws_db_subnet_group.this.name
  vpc_security_group_ids = [aws_security_group.this.id]
  parameter_group_name   = aws_db_parameter_group.this.name
  publicly_accessible    = false

  multi_az = false # ProductionPath=MultiAZ - pilot stays Single-AZ (approved baseline)

  backup_retention_period = var.backup_retention_days
  # Non-overlapping, low-traffic UTC windows - no real pilot traffic exists
  # yet to optimize around further (CP5.3B item 16).
  backup_window      = "06:00-07:00"
  maintenance_window = "sun:07:30-sun:08:30"

  deletion_protection = true
  skip_final_snapshot = false
  # Timestamp-suffixed so re-creating the instance (e.g. after deliberately
  # disabling deletion_protection for a real Homolog rebuild) never collides
  # with a previous final snapshot's identifier.
  final_snapshot_identifier = "ihostpro-${var.environment}-final-${formatdate("YYYYMMDD-hhmmss", timestamp())}"

  auto_minor_version_upgrade  = true
  allow_major_version_upgrade = false

  performance_insights_enabled = false
  monitoring_interval          = 0 # Enhanced Monitoring disabled (CP5 cost/complexity baseline)

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }

  lifecycle {
    # final_snapshot_identifier's timestamp() would otherwise force a
    # replacement plan on every single `terraform plan` - only the
    # snapshot's own future value changes, never the running instance.
    ignore_changes = [final_snapshot_identifier]
  }
}

# --- Application-role connection strings (CP5.3B item 12/26): SAFE_NO_STATE,
# confirmed via `terraform providers schema -json` against the installed
# hashicorp/aws 5.100.0 - aws_secretsmanager_secret_version.secret_string_wo
# genuinely exists and is write-only. The value is the full Npgsql
# connection string (matches every existing appsettings.json
# ConnectionStrings:<Context> shape exactly - zero application code change),
# built from Terraform-known, non-secret pieces (RDS endpoint/port/db name)
# plus an ephemeral-generated password that Terraform never stores in state
# or plan output.
#
# CP5.3C corrective Decision Gate: upgraded from SSL Mode=Require to
# VerifyFull with the RDS CA bundle - every one of docker/{Api,Worker,
# MigrationRunner,DatabaseBootstrap}.Dockerfile now bakes in the official
# AWS global trust bundle at this exact path. Never Trust Server
# Certificate=true. ---
locals {
  rds_ca_bundle_path = "/app/rds-ca/global-bundle.pem"
}

ephemeral "random_password" "app" {
  length  = 32
  special = false # avoid characters that need escaping inside a Npgsql connection string
}

ephemeral "random_password" "migrator" {
  length  = 32
  special = false
}

resource "aws_secretsmanager_secret_version" "app" {
  secret_id        = var.app_secret_arn
  secret_string_wo = "Host=${aws_db_instance.this.address};Port=${aws_db_instance.this.port};Database=${var.database_name};Username=${var.app_role_name};Password=${ephemeral.random_password.app.result};SSL Mode=VerifyFull;Root Certificate=${local.rds_ca_bundle_path}"
  # Bumped from 1 - secret_string_wo has no persisted value for Terraform to
  # diff against, so only a version bump (not a content change alone) tells
  # it to actually rewrite the secret on the next apply.
  secret_string_wo_version = 2
}

resource "aws_secretsmanager_secret_version" "migrator" {
  secret_id                = var.migrator_secret_arn
  secret_string_wo         = "Host=${aws_db_instance.this.address};Port=${aws_db_instance.this.port};Database=${var.database_name};Username=${var.migrator_role_name};Password=${ephemeral.random_password.migrator.result};SSL Mode=VerifyFull;Root Certificate=${local.rds_ca_bundle_path}"
  secret_string_wo_version = 2
}
