terraform {
  required_providers {
    aws = {
      source = "hashicorp/aws"
    }
    random = {
      source  = "hashicorp/random"
      version = ">= 3.0"
    }
  }
}

provider "aws" {
  region = "us-east-1"
}

module "network" {
  source = "git::https://github.com/example/terraform-network.git"
}

resource "aws_security_group" "web" {
  name = "web"

  ingress {
    from_port   = 22
    to_port     = 22
    protocol    = "tcp"
    cidr_blocks = ["10.0.0.0/8", "0.0.0.0/0"]
  }

  ingress {
    from_port   = 8080
    to_port     = 8080
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_instance" "web" {
  ami                         = "ami-0abcdef1234567890"
  instance_type               = "t3.micro"
  associate_public_ip_address = true
  vpc_security_group_ids      = [aws_security_group.web.id]

  user_data = <<-EOT
    #!/bin/bash
    echo "hello {" > /tmp/motd
  EOT
}

resource "aws_db_instance" "main" {
  engine              = "mysql"
  instance_class      = "db.t3.micro"
  publicly_accessible = true

  tags = {
    owner = "web"
  }
}

resource "google_compute_firewall" "ssh" {
  name          = "allow-ssh"
  network       = "default"
  source_ranges = ["0.0.0.0/0"]

  allow {
    protocol = "tcp"
    ports    = ["22", "80"]
  }
}

resource "random_id" "suffix" {
  byte_length = 4
}
