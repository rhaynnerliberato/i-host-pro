data "aws_availability_zones" "available" {
  state = "available"
}

resource "aws_vpc" "this" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = {
    Name        = "ihostpro-${var.environment}-vpc"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_internet_gateway" "this" {
  vpc_id = aws_vpc.this.id

  tags = {
    Name        = "ihostpro-${var.environment}-igw"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_subnet" "public" {
  count                   = var.availability_zone_count
  vpc_id                  = aws_vpc.this.id
  cidr_block              = var.public_subnet_cidrs[count.index]
  availability_zone       = data.aws_availability_zones.available.names[count.index]
  map_public_ip_on_launch = true

  tags = {
    Name        = "ihostpro-${var.environment}-public-${count.index + 1}"
    Project     = var.project
    Environment = var.environment
    Tier        = "public"
    ManagedBy   = "Terraform"
  }
}

resource "aws_subnet" "private" {
  count             = var.availability_zone_count
  vpc_id            = aws_vpc.this.id
  cidr_block        = var.private_subnet_cidrs[count.index]
  availability_zone = data.aws_availability_zones.available.names[count.index]

  tags = {
    Name        = "ihostpro-${var.environment}-private-${count.index + 1}"
    Project     = var.project
    Environment = var.environment
    Tier        = "private"
    ManagedBy   = "Terraform"
  }
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.this.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.this.id
  }

  tags = {
    Name        = "ihostpro-${var.environment}-public-rt"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_route_table_association" "public" {
  count          = var.availability_zone_count
  subnet_id      = aws_subnet.public[count.index].id
  route_table_id = aws_route_table.public.id
}

# No route to an Internet Gateway or NAT Gateway — approved decision
# (PilotNatGatewayEnabled=false). RDS/ElastiCache/Amazon MQ only ever need
# to be reached from the public-subnet Api/Worker tasks, never the reverse.
resource "aws_route_table" "private" {
  vpc_id = aws_vpc.this.id

  tags = {
    Name        = "ihostpro-${var.environment}-private-rt"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_route_table_association" "private" {
  count          = var.availability_zone_count
  subnet_id      = aws_subnet.private[count.index].id
  route_table_id = aws_route_table.private.id
}

resource "aws_security_group" "alb" {
  name        = "ihostpro-${var.environment}-alb"
  description = "Inbound HTTPS from the internet to the ALB only."
  vpc_id      = aws_vpc.this.id

  ingress {
    description = "HTTPS from anywhere"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  # CP5.3D-B item 13: port 80 exists only to be redirected to 443 by the
  # ALB's own listener (modules/alb's aws_lb_listener.http_redirect) - never
  # forwarded to a target group. Description text left unchanged (immutable/
  # ForceNew on this resource - confirmed the hard way during CP5.3C).
  # Gated on create_alb_http_ingress (item 6/7: false during the B1-isolated
  # Route53-zone-only plan/apply, true once B2/the ALB actually exists).
  dynamic "ingress" {
    for_each = var.create_alb_http_ingress ? [1] : []
    content {
      description = "HTTP from anywhere (redirect-only, see http_redirect listener)"
      from_port   = 80
      to_port     = 80
      protocol    = "tcp"
      cidr_blocks = ["0.0.0.0/0"]
    }
  }

  egress {
    description = "To Api tasks"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name        = "ihostpro-${var.environment}-alb-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_security_group" "api" {
  name        = "ihostpro-${var.environment}-api"
  description = "Api Fargate tasks - inbound ONLY from the ALB security group. Task has a public IP (PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP) but this SG is what actually prevents public reachability - never add a 0.0.0.0/0 ingress rule here."
  vpc_id      = aws_vpc.this.id

  ingress {
    description     = "App port from ALB only"
    from_port       = 8080
    to_port         = 8080
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
  }

  egress {
    description = "Outbound to AWS APIs, Anthropic, Meta, Postgres, Redis, RabbitMQ"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name        = "ihostpro-${var.environment}-api-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_security_group" "worker" {
  name        = "ihostpro-${var.environment}-worker"
  description = "Worker Fargate tasks - zero inbound rules, outbound only. Also has a public IP under PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP, but with no ingress rule at all it is unreachable from anywhere, including the ALB."
  vpc_id      = aws_vpc.this.id

  egress {
    description = "Outbound to AWS APIs, Anthropic, Meta, Postgres, Redis, RabbitMQ"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name        = "ihostpro-${var.environment}-worker-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

resource "aws_security_group" "migrationrunner" {
  name        = "ihostpro-${var.environment}-migrationrunner"
  description = "MigrationRunner one-off Fargate task - zero inbound rules, outbound only. Public subnet + public IP (no NAT), same PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP model as Api/Worker - it must reach ECR/CloudWatch/AWS APIs to run at all."
  vpc_id      = aws_vpc.this.id

  egress {
    description = "Outbound to RDS, Amazon MQ, AWS APIs (ECR/CloudWatch/Secrets Manager)"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name        = "ihostpro-${var.environment}-migrationrunner-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# CP5.3C corrective Decision Gate item 22-23: same PUBLIC_TASK_ENI_LOCKED_
# SECURITY_GROUP model as MigrationRunner (zero inbound, public subnet, no
# NAT) - a dedicated SG rather than reusing MigrationRunner's, so the two
# one-off tasks stay independently auditable/revocable at the network layer,
# matching how they already have independent IAM task roles. Egress is
# broad (-1/0.0.0.0/0), not narrowed to the four named AWS service ports -
# deliberately consistent with the existing MigrationRunner/Api/Worker SGs,
# which all rely on zero inbound rules for protection, not restricted
# egress (AWS service endpoints have no small, stable CIDR list to enumerate
# without a NAT/VPC-endpoint redesign, out of scope here).
resource "aws_security_group" "database_bootstrap" {
  name        = "ihostpro-${var.environment}-database-bootstrap"
  description = "DatabaseBootstrap one-off Fargate task - zero inbound rules, outbound only. Public subnet + public IP (no NAT), same PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP model as MigrationRunner/Api/Worker."
  vpc_id      = aws_vpc.this.id

  egress {
    description = "Outbound to RDS, AWS APIs (ECR/CloudWatch/Secrets Manager)"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name        = "ihostpro-${var.environment}-database-bootstrap-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# CP5.3C RabbitMQ credential rotation subgate: same PUBLIC_TASK_ENI_LOCKED_
# SECURITY_GROUP model, dedicated SG for the same independent-auditability
# reason as database_bootstrap above. This is the only one-off task that
# needs Amazon MQ's Management API (HTTPS/443, not the AMQPS/5671 the other
# tasks use) - kept as its own SG so that inbound grant on the broker's SG
# is scoped to exactly this task, not shared with MigrationRunner's AMQPS
# access.
resource "aws_security_group" "rabbitmq_rotation" {
  name        = "ihostpro-${var.environment}-rabbitmq-rotation"
  description = "RabbitMQ credential rotation one-off Fargate task - zero inbound rules, outbound only. Public subnet + public IP (no NAT), same PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP model as MigrationRunner/DatabaseBootstrap."
  vpc_id      = aws_vpc.this.id

  egress {
    description = "Outbound to Amazon MQ Management API, AWS APIs (ECR/CloudWatch/Secrets Manager)"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name        = "ihostpro-${var.environment}-rabbitmq-rotation-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# CP5.3D-C corrective Decision Gate: same PUBLIC_TASK_ENI_LOCKED_SECURITY_
# GROUP model, dedicated SG for the same independent-auditability reason as
# database_bootstrap/rabbitmq_rotation above - this one-off task only ever
# talks to RDS (as ihostpro_app) and AWS APIs, never Amazon MQ/Redis.
resource "aws_security_group" "tenant_provisioning" {
  name        = "ihostpro-${var.environment}-tenant-provisioning"
  description = "Tenant/admin provisioning one-off Fargate task - zero inbound rules, outbound only. Public subnet + public IP (no NAT), same PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP model as DatabaseBootstrap/MigrationRunner."
  vpc_id      = aws_vpc.this.id

  egress {
    description = "Outbound to RDS, AWS APIs (ECR/CloudWatch/Secrets Manager)"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name        = "ihostpro-${var.environment}-tenant-provisioning-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}

# CP5.3D-D corrective Decision Gate: same PUBLIC_TASK_ENI_LOCKED_SECURITY_
# GROUP model - HomologScenarioProvisioning=TEST_FIXTURE_ONLY, only ever
# talks to RDS (as ihostpro_app) and AWS APIs, same shape as
# tenant_provisioning above.
resource "aws_security_group" "homolog_scenario_provisioning" {
  name        = "ihostpro-${var.environment}-homolog-scenario-provisioning"
  description = "Homolog test-fixture provisioning one-off Fargate task - zero inbound rules, outbound only. Public subnet + public IP (no NAT), same PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP model as the other one-off tools."
  vpc_id      = aws_vpc.this.id

  egress {
    description = "Outbound to RDS, AWS APIs (ECR/CloudWatch/Secrets Manager)"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name        = "ihostpro-${var.environment}-homolog-scenario-provisioning-sg"
    Project     = var.project
    Environment = var.environment
    ManagedBy   = "Terraform"
  }
}
