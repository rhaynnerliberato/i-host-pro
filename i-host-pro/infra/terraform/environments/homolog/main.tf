# CP5.2 scope: network foundation + ECR only. RDS/ElastiCache/Amazon MQ/ECS
# services/ALB/CloudFront are deliberately NOT created here yet — see
# ../../README.md for the full CP5 sequencing.

module "network" {
  source = "../../modules/network"

  environment = "homolog"
}

module "ecr" {
  source = "../../modules/ecr"

  environment = "homolog"
}
