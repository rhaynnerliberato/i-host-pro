variable "project" {
  type    = string
  default = "iHostPro"
}

variable "alert_email" {
  description = "Email address to receive AWS Budget alert notifications. No default — must be provided explicitly by the caller, never invented."
  type        = string
}

variable "warning_threshold_usd" {
  description = "Approved operational alert threshold (not a spend authorization). CP5.3D-A Decision Gate: raised from 180 to 350 - the real FullPilotMonthlyCostEstimate (~$304-322, Pricing API-verified for RDS/Valkey/Amazon MQ/ECS/ALB) already exceeded the old value, which would have fired on nominal operation, not real anomalies."
  type        = number
  default     = 350
}

variable "critical_threshold_usd" {
  description = "Approved operational alert threshold (not a spend authorization) — also used as the AWS Budget's monthly limit_amount, purely for percentage-based alerting. CP5.3D-A Decision Gate: raised from 250 to 450, giving margin above the estimate for ALB LCU/log-volume uncertainty (both traffic-dependent, not yet measurable pre-pilot) while still catching genuinely anomalous billing."
  type        = number
  default     = 450
}
