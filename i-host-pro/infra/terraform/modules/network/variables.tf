variable "environment" {
  description = "Environment name (e.g. homolog, production). Used for naming and tagging."
  type        = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}

variable "vpc_cidr" {
  description = "CIDR block for the VPC."
  type        = string
  default     = "10.20.0.0/16"
}

variable "public_subnet_cidrs" {
  description = "CIDR blocks for public subnets, one per AZ. Hosts the ALB and the Api/Worker Fargate task ENIs — approved PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP model: public IP, zero public inbound enforced by Security Groups, not by subnet routing."
  type        = list(string)
  default     = ["10.20.0.0/20", "10.20.16.0/20"]
}

variable "private_subnet_cidrs" {
  description = "CIDR blocks for private subnets, one per AZ. Reserved for future RDS/ElastiCache/Amazon MQ — no internet route of any kind (no NAT Gateway; PilotNatGatewayEnabled=false), VPC-internal traffic only."
  type        = list(string)
  default     = ["10.20.128.0/20", "10.20.144.0/20"]
}

variable "availability_zone_count" {
  description = "Number of AZs to span. The topology is prepared for >=2 AZs even though pilot workloads run single-instance (SINGLE_API_SINGLE_WORKER) — this does NOT imply HA today, only that a future scale-out does not require re-cabling the network."
  type        = number
  default     = 2
}
