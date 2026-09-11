# CP6 Plan A (Frontend Hosting): this module is now instantiated twice - once
# with the default (sa-east-1) provider for api.homolog's ALB certificate,
# once with an aws.us_east_1-aliased provider for the CloudFront certificate,
# which AWS requires to be issued in us-east-1 regardless of where the rest
# of the environment lives. An explicit required_providers entry is what lets
# a caller pass a non-default provider via the `providers = { aws = ... }`
# map without Terraform warning that "aws" was never declared configurable.
terraform {
  required_providers {
    aws = {
      source = "hashicorp/aws"
    }
  }
}
