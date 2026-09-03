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
    password = var.broker_password
  }

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}
