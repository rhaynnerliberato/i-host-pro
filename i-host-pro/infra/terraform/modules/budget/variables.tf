variable "project" {
  type    = string
  default = "iHostPro"
}

variable "alert_email" {
  description = "Email address to receive AWS Budget alert notifications. No default — must be provided explicitly by the caller, never invented."
  type        = string
}

variable "warning_threshold_usd" {
  description = "Approved operational alert threshold (not a spend authorization)."
  type        = number
  default     = 180
}

variable "critical_threshold_usd" {
  description = "Approved operational alert threshold (not a spend authorization) — also used as the AWS Budget's monthly limit_amount, purely for percentage-based alerting."
  type        = number
  default     = 250
}
