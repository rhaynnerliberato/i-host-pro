variable "domain_name" {
  description = "The registered apex domain (e.g. ihostpro.com.br). Registrar stays Registro.br - only DNS authority moves here."
  type        = string
}

variable "project" {
  type    = string
  default = "iHostPro"
}
