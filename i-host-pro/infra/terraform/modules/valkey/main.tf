resource "aws_elasticache_subnet_group" "this" {
  name       = "ihostpro-${var.environment}-valkey"
  subnet_ids = var.private_subnet_ids

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_security_group" "this" {
  name        = "ihostpro-${var.environment}-valkey"
  description = "ElastiCache Valkey - inbound tcp/6379 only from Api/Worker task SGs, never a public CIDR, never MigrationRunner."
  vpc_id      = var.vpc_id

  ingress {
    description     = "Valkey from Api/Worker"
    from_port       = 6379
    to_port         = 6379
    protocol        = "tcp"
    security_groups = var.allowed_security_group_ids
  }

  tags = {
    Name        = "ihostpro-${var.environment}-valkey-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# Single-node replication group (aws_elasticache_replication_group, not
# aws_elasticache_cluster - only the replication_group resource exposes
# auth_token/transit encryption at all, confirmed via the installed
# provider's schema). num_cache_clusters=1 = pilot single-node baseline,
# RedisPilotHA=false, a known/accepted SPOF.
resource "aws_elasticache_replication_group" "this" {
  replication_group_id = "ihostpro-${var.environment}-valkey"
  description          = "iHostPro ${var.environment} Valkey (rate limiting, session revocation, policy cache)"

  engine         = "valkey"
  engine_version = var.engine_version
  node_type      = var.node_type

  num_cache_clusters = 1

  subnet_group_name  = aws_elasticache_subnet_group.this.name
  security_group_ids = [aws_security_group.this.id]

  at_rest_encryption_enabled = true
  transit_encryption_enabled = true
  # CP5.3B Decision Gate: AUTH intentionally left unset (see variables.tf) -
  # setting a real value here requires resolving the state-exposure finding
  # first. Transit + at-rest encryption are both already enabled regardless.
  auth_token = var.auth_token

  auto_minor_version_upgrade = true

  # SnapshotRequiredForPilot=false (approved baseline) - cache data is
  # disposable/regenerable; RedisDownCorePolicyFlowWorks=true already
  # proven, so a cold cache after a node replacement is an accepted,
  # non-data-loss condition.
  snapshot_retention_limit = 0

  tags = {
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}
