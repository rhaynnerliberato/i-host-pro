resource "aws_security_group" "this" {
  name        = "ihostpro-${var.environment}-amazon-mq"
  description = "Amazon MQ RabbitMQ - inbound tcp/5671 (AMQPS) only from Api/Worker/MigrationRunner task SGs, never a public CIDR."
  vpc_id      = var.vpc_id

  ingress {
    description     = "AMQPS from application tasks"
    from_port       = 5671
    to_port         = 5671
    protocol        = "tcp"
    security_groups = var.allowed_security_group_ids
  }

  tags = {
    Name        = "ihostpro-${var.environment}-amazon-mq-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# CP5.3B Decision Gate: ACCEPTED_PILOT_SECURITY_EXCEPTION.
#
# aws_mq_broker's nested user.password is REQUIRED and has NO write-only
# variant in hashicorp/aws 6.62.0 (confirmed via `terraform providers schema
# -json` - unlike aws_elasticache_replication_group.auth_token_wo, which DOES
# exist as of this same version). A regular (non-ephemeral) random_password
# is used deliberately here - an ephemeral value cannot be assigned to a
# non-write-only argument at all (Terraform rejects it), so this password
# WILL be recorded in Terraform state. Approved explicitly for the pilot
# because: the broker create API requires a user/password at creation time;
# no password_wo alternative exists; keeping the broker under Terraform
# management was preferred over an out-of-band bootstrap; the state backend
# is already encrypted (SSE-S3), versioned, and access-restricted.
#
# This is a BOOTSTRAP credential only - RabbitMqFinalCredentialInTerraformState=false
# is the target. Real, sourced finding (not assumed): the AWS MQ control-plane
# `aws mq update-user` API does NOT apply to RabbitMQ brokers (RabbitMQ
# broker users are managed exclusively via RabbitMQ's OWN management HTTP
# API - `PUT /api/users/{username}` - or its web console, never the AWS MQ
# API, once the broker exists). CP5.3C's rotation subgate must call THAT
# API (from inside the VPC, e.g. a dedicated one-off ECS task reachable to
# the broker's private management endpoint), write the new password to this
# same Secrets Manager secret, and invalidate the bootstrap credential -
# never rely on `aws mq update-user`, which silently would not work.
resource "random_password" "bootstrap" {
  length  = 32
  special = false
}

resource "aws_mq_broker" "this" {
  broker_name = "ihostpro-${var.environment}"

  engine_type        = "RABBITMQ"
  engine_version     = var.engine_version
  host_instance_type = var.instance_type
  deployment_mode    = "SINGLE_INSTANCE" # RabbitMqPilotHA=false, RabbitMqPilotSinglePointOfFailure=true - accepted baseline

  publicly_accessible = false
  subnet_ids          = [var.private_subnet_ids[0]] # SINGLE_INSTANCE uses exactly one subnet
  security_groups     = [aws_security_group.this.id]

  auto_minor_version_upgrade = true

  # AWS-owned key (default) - no customer-managed CMK: no concrete
  # requirement drives the added key-policy/rotation complexity at this
  # stage (same "avoid complexity without a real requirement" reasoning
  # already applied to the Terraform state bucket's SSE-S3 choice).
  encryption_options {
    use_aws_owned_key = true
  }

  user {
    username = var.broker_username
    password = random_password.bootstrap.result
  }

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

locals {
  # instances[0].endpoints is a list of URIs (amqp+ssl / amqps / stomp+ssl /
  # etc, depending on engine); the AMQPS one is what UseIHostProRabbitMq
  # needs, stripped down to a bare hostname (RabbitMq:Host expects only the
  # host, never a scheme/port - mirrors the local docker-compose shape).
  amqps_endpoint = [for e in aws_mq_broker.this.instances[0].endpoints : e if startswith(e, "amqps://")][0]
  amqps_hostname = split(":", replace(local.amqps_endpoint, "amqps://", ""))[0]
}

# AMQPS connection info (matches the existing RabbitMq:Host/Port/Username/
# Password/UseTls=true config shape from Fase 12 CP5.1 - zero code change).
# Bootstrap credential only, per the note above - CP5.3C rotation replaces
# this value.
resource "aws_secretsmanager_secret_version" "rabbitmq" {
  secret_id = var.rabbitmq_secret_arn
  secret_string = jsonencode({
    host        = local.amqps_hostname
    port        = 5671
    virtualHost = "/"
    username    = var.broker_username
    password    = random_password.bootstrap.result
    useTls      = true
  })
}
