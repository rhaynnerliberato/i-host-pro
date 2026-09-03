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
