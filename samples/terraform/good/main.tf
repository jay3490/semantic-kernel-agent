variable "region" {
  type    = string
  default = "eu-west-1"
}

variable "environment" {
  type = string
}

module "vpc" {
  source  = "terraform-aws-modules/vpc/aws"
  version = "5.8.1"

  name            = "orders-${var.environment}"
  cidr            = "10.20.0.0/16"
  azs             = ["eu-west-1a", "eu-west-1b"]
  private_subnets = ["10.20.1.0/24", "10.20.2.0/24"]
  public_subnets  = ["10.20.101.0/24", "10.20.102.0/24"]
}

# HTTPS from anywhere is expected on a public load balancer.
resource "aws_security_group" "alb" {
  name   = "orders-alb"
  vpc_id = module.vpc.vpc_id

  ingress {
    description = "HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_db_instance" "orders" {
  identifier          = "orders-${var.environment}"
  engine              = "postgres"
  engine_version      = "16.3"
  instance_class      = "db.t4g.medium"
  allocated_storage   = 50
  publicly_accessible = false
  storage_encrypted   = true
}
