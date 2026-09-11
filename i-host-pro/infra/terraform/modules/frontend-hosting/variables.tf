variable "environment" {
  type = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}

# CP6 Plan A: the Angular SPA's own custom domain (e.g.
# app.homolog.ihostpro.com.br) - the CloudFront distribution's alias, never a
# second/duplicate cert domain from what Route53/ACM already model elsewhere.
variable "domain_name" {
  description = "Custom domain the CloudFront distribution serves (also the sole SAN on the ACM certificate)."
  type        = string
}

# CP6 Plan A: CloudFront requires its viewer certificate to exist in
# us-east-1 regardless of where every other resource in this environment
# lives (sa-east-1) - the caller is responsible for requesting this ACM
# certificate against an aws.us_east_1-aliased provider. No default: a
# fake/placeholder certificate is never created, same discipline as
# modules/alb's certificate_arn.
variable "certificate_arn" {
  description = "ACM certificate ARN (must be issued in us-east-1) for the CloudFront viewer certificate."
  type        = string
}

# CP6 Plan A decision gate (explicit correction after review): PriceClass_100
# was the original cost-minimizing proposal, but it excludes South American
# edge locations entirely - unacceptable for a Stage 1 pilot whose real
# users are in Brazil. PriceClass_200 adds South America (plus Asia/Middle
# East/Africa) without going to the full cost of PriceClass_All, which is
# not approved for this pilot either.
variable "price_class" {
  type    = string
  default = "PriceClass_200"
}
