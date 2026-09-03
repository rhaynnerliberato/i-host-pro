# Production environment — deliberately deferred

No Terraform root exists here yet. Per the Fase 12 CP5.2 mandate, this
checkpoint's scope is Homologação foundation only.

What already exists that touches Production:

- `ihostpro-production-deploy` IAM role (created by
  `../global`/`modules/github-oidc`) — trust policy restricted to the
  `production` GitHub Environment, **not used for any real deploy yet**.

What is intentionally NOT created:

- Production VPC/subnets/security groups
- Production RDS/ElastiCache/Amazon MQ
- Production ECS services/ALB/CloudFront
- Production Route53/ACM (also blocked on `BaseDomain=USER_DECISION_PENDING`)

When Production provisioning is authorized, this directory should mirror
`../homolog`'s structure (`versions.tf`, `variables.tf`, `main.tf`,
`outputs.tf`), reusing the same `../../modules/network` and
`../../modules/ecr` modules with `environment = "production"`, plus the
Multi-AZ/HA upgrades already flagged in the CP5 cost/network reports
(Multi-AZ RDS, ElastiCache replica, Amazon MQ active/standby) — none of
which are part of this checkpoint.
