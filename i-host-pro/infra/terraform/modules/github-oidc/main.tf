data "tls_certificate" "github" {
  url = "https://token.actions.githubusercontent.com/.well-known/openid-configuration"
}

resource "aws_iam_openid_connect_provider" "github" {
  url             = "https://token.actions.githubusercontent.com"
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = [data.tls_certificate.github.certificates[0].sha1_fingerprint]

  tags = {
    Project   = var.project
    ManagedBy = "Terraform"
  }
}

# Trust policy per environment: only workflow runs targeting that exact
# GitHub Environment may assume the role — never a bare branch/tag condition,
# and never a wildcard subject. The subject uses GitHub's immutable-ID format
# (repo:<org>@<owner-id>/<repo>@<repo-id>:environment:<name>) — see
# github_owner_id/github_repo_id's own description for why the plain-name
# format doesn't match this repository's real token.
# Production's GitHub Environment should additionally have required-reviewer
# protection configured in GitHub itself (Terraform cannot express that).
data "aws_iam_policy_document" "trust" {
  for_each = var.environments

  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    condition {
      test     = "StringLike"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_org}@${var.github_owner_id}/${var.github_repo}@${var.github_repo_id}:environment:${each.value}"]
    }
  }
}

resource "aws_iam_role" "deploy" {
  for_each = var.environments

  name               = "ihostpro-${each.key}-deploy"
  assume_role_policy = data.aws_iam_policy_document.trust[each.key].json

  tags = {
    Project     = var.project
    Environment = each.key
    ManagedBy   = "Terraform"
  }
}

# Scoped to what the deploy pipeline actually needs (build+push image,
# update the ECS service, describe/register task definitions) — never the
# broad AdministratorAccess used for human bootstrap. Two documented
# exceptions use Resource="*" because the underlying AWS action does not
# support resource-level restriction at all (ecr:GetAuthorizationToken) or
# not for this specific action (ecs:DescribeTaskDefinition /
# ecs:RegisterTaskDefinition) — real AWS IAM limitations, not an
# oversight. iam:PassRole is scoped tightly by naming convention AND by
# iam:PassedToService as the actual enforcement boundary.
data "aws_iam_policy_document" "deploy_permissions" {
  for_each = var.environments

  statement {
    sid       = "EcrAuth"
    effect    = "Allow"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    sid    = "EcrPushPull"
    effect = "Allow"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:GetDownloadUrlForLayer",
      "ecr:BatchGetImage",
      "ecr:PutImage",
      "ecr:InitiateLayerUpload",
      "ecr:UploadLayerPart",
      "ecr:CompleteLayerUpload",
    ]
    resources = ["arn:aws:ecr:*:*:repository/ihostpro-${each.key}-*"]
  }

  statement {
    sid    = "EcsServiceUpdate"
    effect = "Allow"
    actions = [
      "ecs:UpdateService",
      "ecs:DescribeServices",
    ]
    resources = ["arn:aws:ecs:*:*:service/ihostpro-${each.key}-cluster/*"]
  }

  statement {
    sid    = "EcsTaskDefinitions"
    effect = "Allow"
    actions = [
      "ecs:DescribeTaskDefinition",
      "ecs:RegisterTaskDefinition",
    ]
    resources = ["*"]
  }

  statement {
    sid     = "PassEcsTaskRoles"
    effect  = "Allow"
    actions = ["iam:PassRole"]
    # CP5.3C corrective Decision Gate item 8-12 audit: ihostpro-<env>-ecs-*
    # matches only the shared execution role - none of the individual task
    # roles (modules/ecs-iam) are named that way. Extended narrowly to
    # exactly the two CP5.3C one-off task roles (MigrationRunner,
    # DatabaseBootstrap) - api-task/worker-task stay out until CP5.3D
    # actually authorizes their services.
    resources = [
      "arn:aws:iam::*:role/ihostpro-${each.key}-ecs-*",
      "arn:aws:iam::*:role/ihostpro-${each.key}-migrationrunner-task",
      "arn:aws:iam::*:role/ihostpro-${each.key}-database-bootstrap-task",
    ]

    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["ecs-tasks.amazonaws.com"]
    }
  }

  # CP5.3C corrective Decision Gate item 11 audit: ecs:RunTask/DescribeTasks
  # were entirely absent - the two one-off task definitions this checkpoint
  # creates would otherwise be unusable by this deploy role. Scoped to
  # exactly those two task-definition families and the one cluster.
  statement {
    sid     = "EcsRunOneOffTasks"
    effect  = "Allow"
    actions = ["ecs:RunTask"]
    resources = [
      "arn:aws:ecs:*:*:task-definition/ihostpro-${each.key}-migrationrunner:*",
      "arn:aws:ecs:*:*:task-definition/ihostpro-${each.key}-database-bootstrap:*",
    ]

    condition {
      test     = "ArnEquals"
      variable = "ecs:cluster"
      values   = ["arn:aws:ecs:*:*:cluster/ihostpro-${each.key}-cluster"]
    }
  }

  statement {
    sid       = "EcsDescribeOneOffTasks"
    effect    = "Allow"
    actions   = ["ecs:DescribeTasks"]
    resources = ["arn:aws:ecs:*:*:task/ihostpro-${each.key}-cluster/*"]
  }

  # CP6 Plan A (Frontend Hosting): bucket name is exact and deterministic
  # (matches modules/frontend-hosting's own "ihostpro-<env>-frontend-<account
  # id>" naming), same predictable-naming-convention approach already used
  # for every ECR/ECS resource above - never a cross-Terraform-root state
  # reference (github-oidc lives in environments/global, frontend-hosting in
  # environments/homolog - two separate state files with no existing
  # linkage in this codebase).
  statement {
    sid    = "FrontendDeployS3"
    effect = "Allow"
    actions = [
      "s3:PutObject",
      "s3:DeleteObject",
      "s3:ListBucket",
    ]
    resources = [
      "arn:aws:s3:::ihostpro-${each.key}-frontend-${data.aws_caller_identity.current.account_id}",
      "arn:aws:s3:::ihostpro-${each.key}-frontend-${data.aws_caller_identity.current.account_id}/*",
    ]
  }

  # CP6 Plan A: unlike the S3 bucket name above, a CloudFront distribution ID
  # is AWS-assigned and opaque - it cannot be predicted by naming convention,
  # and (per the same cross-root limitation noted above) this policy has no
  # way to reference environments/homolog's real distribution ARN output
  # without introducing a new terraform_remote_state linkage that does not
  # exist anywhere in this codebase today. Scoped to this AWS account only
  # (never Resource="*") and to exactly one action - distribution IDs are not
  # secret/sensitive, so an account-scoped wildcard on the ID segment carries
  # no real exposure beyond "this role may invalidate any CloudFront
  # distribution in this account," which today is exactly one (the frontend's).
  # Flagged explicitly for review rather than silently treated as final.
  statement {
    sid       = "FrontendDeployCloudFrontInvalidation"
    effect    = "Allow"
    actions   = ["cloudfront:CreateInvalidation"]
    resources = ["arn:aws:cloudfront::${data.aws_caller_identity.current.account_id}:distribution/*"]
  }
}

data "aws_caller_identity" "current" {}

resource "aws_iam_role_policy" "deploy" {
  for_each = var.environments

  name   = "ihostpro-${each.key}-deploy-permissions"
  role   = aws_iam_role.deploy[each.key].id
  policy = data.aws_iam_policy_document.deploy_permissions[each.key].json
}
