output "oidc_provider_arn" {
  value = aws_iam_openid_connect_provider.github.arn
}

output "deploy_role_arns" {
  value = { for k, v in aws_iam_role.deploy : k => v.arn }
}
