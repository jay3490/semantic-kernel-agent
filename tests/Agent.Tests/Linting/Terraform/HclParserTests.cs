using Agent.Core.Linting.Terraform;

namespace Agent.Tests.Linting.Terraform;

public sealed class HclParserTests
{
    [Fact]
    public void Reads_blocks_labels_attributes_and_lines()
    {
        const string hcl = """
            # comment
            resource "aws_instance" "web" {
              ami   = "ami-123" // trailing comment
              count = 2

              network_interface {
                device_index = 0
              }
            }
            """;

        var body = HclParser.Parse(hcl);

        var resource = Assert.Single(body.Blocks);
        Assert.Equal(("resource", 2), (resource.Type, resource.Line));
        Assert.Equal(["aws_instance", "web"], resource.Labels);
        Assert.Equal("\"ami-123\"", resource.Body.Attribute("ami")!.Expression);
        Assert.Equal(4, resource.Body.Attribute("count")!.Line);
        Assert.Equal("0", Assert.Single(resource.Body.BlocksOfType("network_interface")).Body.Attribute("device_index")!.Expression);
    }

    [Fact]
    public void Keeps_multi_line_lists_objects_and_heredocs_as_one_expression()
    {
        const string hcl = """
            locals {
              cidrs = [
                "10.0.0.0/8",
                "0.0.0.0/0",
              ]
              tags = {
                owner = "team-a" # }
                cost  = "${var.cost_center}-{x}"
              }
              script = <<-EOT
                echo "{ not a block"
              EOT
              after = true
            }
            """;

        var locals = Assert.Single(HclParser.Parse(hcl).Blocks).Body;

        Assert.Equal(["10.0.0.0/8", "0.0.0.0/0"], HclValues.StringList(locals.Attribute("cidrs")!.Expression));
        Assert.StartsWith("{", locals.Attribute("tags")!.Expression);
        Assert.Contains("not a block", locals.Attribute("script")!.Expression);
        Assert.Equal(13, locals.Attribute("after")!.Line);
    }

    [Fact]
    public void Parses_one_line_blocks_and_object_expressions()
    {
        var body = HclParser.Parse("terraform { required_providers { aws = { source = \"hashicorp/aws\", version = \"~> 5.0\" } } }");

        var providers = body.Blocks[0].Body.Blocks[0].Body;
        var aws = HclParser.ParseObject(providers.Attribute("aws")!.Expression, 1);
        Assert.Equal("~> 5.0", HclValues.String(aws.Attribute("version")!.Expression));
        Assert.Equal("hashicorp/aws", HclValues.String(aws.Attribute("source")!.Expression));
    }

    [Theory]
    [InlineData("resource \"a\" \"b\" {\n  x = 1\n", 1)]
    [InlineData("x = \"unterminated\n", 1)]
    [InlineData("}\n", 1)]
    [InlineData("a = [1, 2\n", 1)]
    [InlineData("\n\n/* never closed", 3)]
    public void Malformed_input_raises_a_syntax_error_with_its_line(string hcl, int line)
    {
        var error = Assert.Throws<HclSyntaxException>(() => HclParser.Parse(hcl));

        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData("\"plain\"", "plain")]
    [InlineData("\"with ${var.x}\"", null)]
    [InlineData("var.x", null)]
    public void String_values_are_only_read_from_literals(string expression, string? expected) =>
        Assert.Equal(expected, HclValues.String(expression));
}
