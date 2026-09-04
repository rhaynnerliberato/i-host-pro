variable "domain_name" {
  description = "The exact hostname the certificate must cover (e.g. api.homolog.ihostpro.com.br)."
  type        = string
}

variable "zone_id" {
  description = "Route53 hosted zone id where the DNS validation record is created."
  type        = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}
