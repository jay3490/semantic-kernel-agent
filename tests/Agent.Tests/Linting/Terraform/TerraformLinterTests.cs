using Agent.Core.Linting;
using Agent.Core.Linting.Terraform;

namespace Agent.Tests.Linting.Terraform;

public sealed class TerraformLinterTests
{
    private readonly TerraformLinter _linter = new();

    private static TerraformFile Sample(string path) => new(Path.GetFileName(path), Samples.Read(Path.Combine("terraform", path)));

    private LintReport LintBad() => _linter.Lint([Sample("bad/main.tf")]);

    [Fact]
    public void Good_module_passes_with_only_an_informational_note_for_public_https()
    {
        var report = _linter.Lint([Sample("good/main.tf"), Sample("good/versions.tf")]);

        Assert.True(report.Passed);
        var https = Assert.Single(report.Findings);
        Assert.Equal(("TF010", Severity.Info, "main.tf"), (https.RuleId, https.Severity, https.File));
        Assert.Contains("port 443", https.Message);
    }

    [Fact]
    public void Bad_module_reports_every_rule()
    {
        var report = LintBad();

        Assert.Equal(["TF001", "TF001", "TF002", "TF003", "TF010", "TF010", "TF010", "TF011", "TF011", "TF020", "TF020"],
            report.Findings.Select(f => f.RuleId).Order());
        Assert.Equal((3, 7, 1), (report.Errors, report.Warnings, report.Infos));
    }

    [Fact]
    public void Providers_without_a_pin_or_declaration_are_reported_where_they_appear()
    {
        var findings = LintBad().Findings.Where(f => f.RuleId is "TF001" or "TF002").ToDictionary(f => f.Target!);

        Assert.Equal(("TF001", 3), (findings["provider aws"].RuleId, findings["provider aws"].Line));
        Assert.Equal(("TF002", 6), (findings["provider random"].RuleId, findings["provider random"].Line));
        Assert.Equal(61, findings["provider google"].Line);
        Assert.Contains("required_providers", findings["provider google"].Message);
    }

    [Fact]
    public void Open_ingress_is_an_error_for_admin_ports_and_a_warning_for_others()
    {
        var ingress = LintBad().Findings.Where(f => f.RuleId == "TF010").ToDictionary(f => f.Line!.Value);

        Assert.Equal(Severity.Error, ingress[28].Severity);
        Assert.Contains("port 22", ingress[28].Message);
        Assert.Equal(Severity.Warning, ingress[35].Severity);
        Assert.Equal(Severity.Error, ingress[64].Severity);
        Assert.Equal("google_compute_firewall.ssh", ingress[64].Target);
    }

    [Fact]
    public void Public_ips_and_databases_are_flagged()
    {
        var exposure = LintBad().Findings.Where(f => f.RuleId == "TF011").ToDictionary(f => f.Target!);

        Assert.Equal((Severity.Warning, 42), (exposure["aws_instance.web"].Severity, exposure["aws_instance.web"].Line!.Value));
        Assert.Equal((Severity.Error, 54), (exposure["aws_db_instance.main"].Severity, exposure["aws_db_instance.main"].Line!.Value));
    }

    [Fact]
    public void Missing_tags_are_reported_unless_tagged_or_covered_by_default_tags()
    {
        var bad = LintBad().Findings.Where(f => f.RuleId == "TF020").Select(f => f.Target);
        var withDefaults = _linter.Lint("""
            provider "aws" {
              default_tags {
                tags = { owner = "platform" }
              }
            }
            resource "aws_s3_bucket" "logs" {}
            resource "google_storage_bucket" "logs" {}
            """);

        Assert.Equal(["aws_instance.web", "aws_security_group.web"], bad.Order());
        Assert.Equal("google_storage_bucket.logs", Assert.Single(withDefaults.Findings, f => f.RuleId == "TF020").Target);
        Assert.Contains("labels", withDefaults.Findings.First(f => f.RuleId == "TF020").Message);
    }

    [Theory]
    [InlineData("source = \"terraform-aws-modules/vpc/aws\"", true)]
    [InlineData("source = \"terraform-aws-modules/vpc/aws\"\n  version = \"5.8.1\"", false)]
    [InlineData("source = \"git::https://example.com/net.git\"", true)]
    [InlineData("source = \"git::https://example.com/net.git?ref=v1.2.0\"", false)]
    [InlineData("source = \"./modules/net\"", false)]
    public void Modules_must_pin_a_version_or_ref(string arguments, bool flagged)
    {
        var report = _linter.Lint($"module \"net\" {{\n  {arguments}\n}}\n");

        Assert.Equal(flagged, report.Findings.Any(f => f.RuleId == "TF003"));
    }

    [Theory]
    [InlineData("type = \"ingress\"\n  from_port = 0\n  to_port = 0\n  protocol = \"-1\"\n  cidr_blocks = [\"0.0.0.0/0\"]", Severity.Error)]
    [InlineData("type = \"egress\"\n  from_port = 0\n  to_port = 0\n  protocol = \"-1\"\n  cidr_blocks = [\"0.0.0.0/0\"]", null)]
    [InlineData("type = \"ingress\"\n  from_port = 5432\n  to_port = 5432\n  protocol = \"tcp\"\n  cidr_blocks = [\"10.0.0.0/16\"]", null)]
    [InlineData("type = \"ingress\"\n  from_port = 8000\n  to_port = 8100\n  protocol = \"tcp\"\n  ipv6_cidr_blocks = [\"::/0\"]", Severity.Warning)]
    public void Standalone_security_group_rules_are_checked(string arguments, Severity? expected)
    {
        var report = _linter.Lint($"resource \"aws_security_group_rule\" \"r\" {{\n  {arguments}\n}}\n");

        Assert.Equal(expected, report.Findings.SingleOrDefault(f => f.RuleId == "TF010")?.Severity);
    }

    [Fact]
    public void Azure_inbound_allow_rules_from_the_internet_are_checked()
    {
        var report = _linter.Lint("""
            resource "azurerm_network_security_group" "nsg" {
              name = "nsg"
              tags = { env = "dev" }

              security_rule {
                name                   = "rdp"
                direction              = "Inbound"
                access                 = "Allow"
                source_address_prefix  = "*"
                destination_port_range = "3389"
              }

              security_rule {
                name                   = "deny"
                direction              = "Inbound"
                access                 = "Deny"
                source_address_prefix  = "*"
                destination_port_range = "*"
              }
            }
            """);

        var finding = Assert.Single(report.Findings, f => f.RuleId == "TF010");
        Assert.Equal((Severity.Error, 5), (finding.Severity, finding.Line!.Value));
    }

    [Fact]
    public void Unparseable_files_are_reported_and_the_rest_are_still_checked()
    {
        var report = _linter.Lint([new TerraformFile("broken.tf", "resource \"x\" \"y\" {\n"), Sample("bad/main.tf")]);

        var invalid = Assert.Single(report.Findings, f => f.RuleId == "TF000");
        Assert.Equal(("broken.tf", 1), (invalid.File, invalid.Line!.Value));
        Assert.Contains(report.Findings, f => f.RuleId == "TF011");
    }
}
