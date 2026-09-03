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

# CP5.3B (revised): AWS provider upgraded to 6.62.0 specifically because
# aws_elasticache_replication_group.auth_token_wo (write-only) only exists
# from 6.x onward - confirmed via `terraform providers schema -json` against
# the real downloaded provider before upgrading. The same ephemeral password
# feeds both this resource's auth_token_wo AND the ihostpro/homolog/redis
# secret below - neither ever touches Terraform state or plan output.
ephemeral "random_password" "valkey_auth" {
  length  = 32
  special = false # AUTH token must not contain characters StackExchange.Redis's connection-string parser would need escaping
}

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

  # ValkeyPasswordlessAccessAllowed=false: SET (not ROTATE) on this first
  # activation - ROTATE keeps the old (nonexistent) token valid alongside
  # the new one during a transition window, which for a brand-new
  # replication group would mean a real passwordless-equivalent gap. SET
  # applies the token as the only valid credential immediately.
  #
  # auth_token_wo (write-only), not auth_token - the plain auth_token
  # attribute is persisted to state and rejects ephemeral values outright
  # (confirmed by Terraform's own validation error when this was first
  # tried against the wrong attribute name).
  auth_token_wo              = ephemeral.random_password.valkey_auth.result
  auth_token_wo_version      = 1
  auth_token_update_strategy = "SET"

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

# StackExchange.Redis connection-string shape (matches every existing
# Configuration:PolicyCache/RateLimiting:Redis/Identity:SessionRevocationCache
# ConnectionString config value exactly - zero application code change).
resource "aws_secretsmanager_secret_version" "redis" {
  secret_id = var.redis_secret_arn
  # Port is the well-known Redis/Valkey default (6379, never overridden by
  # this module) - aws_elasticache_replication_group has no top-level
  # computed `port` attribute to read back.
  secret_string_wo         = "${aws_elasticache_replication_group.this.primary_endpoint_address}:6379,password=${ephemeral.random_password.valkey_auth.result},ssl=true,abortConnect=false"
  secret_string_wo_version = 1
}
