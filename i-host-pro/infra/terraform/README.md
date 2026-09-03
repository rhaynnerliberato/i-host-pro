# iHostPro — AWS Terraform Foundation (Fase 12, CP5.2)

Status at time of writing: **code only, nothing applied against AWS**.
`terraform fmt`/`validate`/`init -backend=false` have been run; no
`terraform plan`/`apply` has been executed against AWS in this checkpoint.

## Directory structure

```
infra/terraform/
  bootstrap/                 # one-time, local state — creates the S3 state bucket
  modules/
    network/                 # VPC, public+private subnets, IGW, route tables, base SGs
    ecr/                     # ECR repositories (Api, Worker, MigrationRunner)
    github-oidc/              # GitHub Actions OIDC provider + per-environment deploy roles
    budget/                   # AWS Budget + SNS email alert topic
  environments/
    global/                   # account-wide: github-oidc + budget modules
    homolog/                  # network + ecr modules for Homologação
    production/                # deferred — see production/README.md
```

## Bootstrap sequence (chicken-and-egg: the state bucket cannot live in the state it stores)

1. `cd bootstrap && terraform init` (uses local state — no backend config needed).
2. `terraform apply -var="state_bucket_name=<globally-unique-name>"` — creates the S3 bucket only.
3. Note the `state_bucket_name` output.
4. For every environment root (`global`, `homolog`, and later `production`):
   ```
   terraform init \
     -backend-config="bucket=<state_bucket_name from step 3>" \
     -backend-config="region=sa-east-1"
   ```
5. From then on, `bootstrap/`'s own state stays local — it is run again only if the
   state bucket itself needs to change (rare, deliberate).

None of this has been executed yet — `state_bucket_name` has no value chosen.

## State locking

`TerraformVersionInstalled=1.15.8` — native S3 state locking (`use_lockfile = true`,
GA since 1.11) is used in every environment's `backend "s3"` block. No DynamoDB
lock table is created — it is not needed on this Terraform version.

## Encryption

State bucket: `SSE-S3` (`AES256`), not a customer-managed KMS key. For this
stage, a dedicated KMS key adds operational complexity (key policy, rotation,
grants for every principal that reads state) without a concrete requirement
driving it — Documento 21's own "não mais complexo que o necessário"
principle. Revisit if a future compliance requirement demands
customer-managed keys specifically.

## Authentication model

- **Human local access**: `aws login` (AWS CLI 2.36.36+) against the
  `ihostpro-bootstrap-admin` IAM user — temporary, browser-session-based
  credentials, zero long-lived access keys. Never root for routine
  Terraform/CLI work.
- **CI access**: GitHub Actions OIDC (`modules/github-oidc`) — one IAM role
  per environment, trust policy restricted to that exact GitHub Environment
  name and this repository. No `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`
  anywhere.
- **Runtime access**: ECS task IAM roles (not yet created — CP5.2 scope is
  network + ECR only).

`HumanBootstrapAdminAccess=true` (the `ihostpro-bootstrap-admin` IAM user
has `AdministratorAccess`) is accepted **temporarily**, for this bootstrap
stage only. `MustReduceHumanAdminPrivilegeAfterBootstrap=true` — revisit
once the deploy/runtime roles above are in place and daily human work no
longer requires unrestricted access.

## Naming & tagging

Resources: `ihostpro-<environment>-<resource>` (e.g. `ihostpro-homolog-api`).
Tags on every resource: `Project=iHostPro`, `Environment=<environment>`,
`ManagedBy=Terraform`. No `CostCenter`/`Owner` tags — no real value exists
for either yet; adding them now would be inventing data.

## Environment separation

`SINGLE_ACCOUNT_STRONG_LOGICAL_SEPARATION` — one AWS account, Homolog and
Production separated by: separate VPCs (`modules/network` invoked once per
environment), separate Terraform state files (`homolog/terraform.tfstate`
vs. a future `production/terraform.tfstate`), separate IAM deploy roles,
separate resource naming. No database/cache/queue is ever shared between
environments. `AwsMultiAccountIsolation=false`, revisit if team size or a
compliance requirement changes the calculus.

## Network rationale

- **No NAT Gateway** (`PilotNatGatewayEnabled=false`) — Api/Worker Fargate
  tasks sit in **public** subnets with a public IP, but reachability is
  governed entirely by Security Groups, not subnet routing
  (`PUBLIC_TASK_ENI_LOCKED_SECURITY_GROUP`, approved CP5 network gate):
  the Api SG allows inbound only from the ALB SG; the Worker SG allows no
  inbound at all. This avoids ~US$70-120/month in NAT Gateway cost.
- **Private subnets exist** but are unused by anything in this checkpoint —
  reserved for RDS/ElastiCache/Amazon MQ, which will get their own
  Security Group(s) scoped to specific ports (5432/6379/5671) when those
  modules are built; a broad "data services" SG was deliberately **not**
  created now to avoid a wildcard-port rule sitting unused.
- Subnets span 2 AZs even though the pilot runs `SINGLE_API_SINGLE_WORKER`
  (no autoscaling) — topology readiness only, **not** a high-availability
  claim (`PilotHA` remains false for every component below).

## Known pilot SPOFs (unchanged by this checkpoint)

Api (single task), Worker (single task), and — once provisioned in a later
checkpoint — RDS (Single-AZ), Redis (single node), Amazon MQ
(single-instance). None of this checkpoint's resources introduce new ones;
none of them remove the existing ones either.

## Cost impact of this checkpoint's resources

VPC/subnets/route tables/Internet Gateway/Security Groups: **$0** (no
hourly charge for any of these on their own). ECR: storage-based, a few
dollars/month at most for 3 repositories with a 30-image retention policy.
GitHub OIDC provider + IAM roles: **$0** (IAM has no per-resource charge).
AWS Budget: **$0** (budgets and the first two SNS-delivered alerts per
month are free; email delivery has no per-notification charge at this
volume). Net: this checkpoint's foundation adds negligible cost on its own —
the real cost (Fargate, RDS, ElastiCache, Amazon MQ, ALB) comes from
resources explicitly excluded from this checkpoint's scope.

## Open decision required before `global` can be applied

`budget_alert_email` has no default (see `modules/budget/variables.tf`) —
an explicit email address must be supplied; it is intentionally not
invented here (see the CP5.2 report, item 35).
