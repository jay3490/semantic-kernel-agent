namespace Agent.Core.Linting.Terraform;

/// <summary>A Terraform file. <see cref="Path"/> is how findings name it; null for text passed directly.</summary>
public sealed record TerraformFile(string? Path, string Text);

/// <summary>
/// Static checks over the <c>.tf</c> files of one Terraform module (they are read together, because
/// <c>required_providers</c> usually lives in a different file from the resources): providers and modules without
/// a version constraint, ingress open to the whole internet, resources that get a public IP or endpoint, and
/// taggable resources without tags. Nothing is evaluated, so values computed from variables are not judged.
/// </summary>
public sealed class TerraformLinter
{
    public const string Name = "terraform";

    private const string AnyIPv4 = "0.0.0.0/0";
    private const string AnyIPv6 = "::/0";

    /// <summary>Ports that should never be reachable from the whole internet.</summary>
    private static readonly int[] AdminPorts = [22, 3389];

    /// <summary>HTTP and HTTPS: open to the internet on purpose for public load balancers, so only informational.</summary>
    private static readonly int[] WebPorts = [80, 443];

    private static readonly HashSet<string> AnySource = new(StringComparer.OrdinalIgnoreCase) { "*", AnyIPv4, AnyIPv6, "Internet", "Any" };

    /// <summary>Common resource types that accept <c>tags</c> (AWS, Azure) or <c>labels</c> (Google).</summary>
    private static readonly HashSet<string> TaggableTypes = new(StringComparer.Ordinal)
    {
        "aws_instance", "aws_launch_template", "aws_autoscaling_group", "aws_vpc", "aws_subnet", "aws_security_group",
        "aws_internet_gateway", "aws_nat_gateway", "aws_eip", "aws_lb", "aws_lb_target_group", "aws_s3_bucket",
        "aws_db_instance", "aws_rds_cluster", "aws_dynamodb_table", "aws_eks_cluster", "aws_eks_node_group",
        "aws_ecs_cluster", "aws_ecs_service", "aws_lambda_function", "aws_sqs_queue", "aws_sns_topic", "aws_kms_key",
        "aws_ebs_volume", "aws_elasticache_cluster", "aws_cloudwatch_log_group", "aws_ecr_repository",
        "azurerm_resource_group", "azurerm_virtual_network", "azurerm_network_security_group", "azurerm_public_ip",
        "azurerm_linux_virtual_machine", "azurerm_windows_virtual_machine", "azurerm_storage_account",
        "azurerm_kubernetes_cluster", "azurerm_key_vault", "azurerm_postgresql_flexible_server", "azurerm_container_registry",
        "google_compute_instance", "google_compute_disk", "google_storage_bucket", "google_container_cluster",
        "google_sql_database_instance", "google_pubsub_topic",
    };

    public LintReport Lint(string text) => Lint([new TerraformFile(null, text)]);

    public LintReport Lint(IReadOnlyList<TerraformFile> files)
    {
        var findings = new List<LintFinding>();
        var parsed = new List<(TerraformFile File, HclBody Body)>();

        foreach (var file in files)
        {
            try
            {
                parsed.Add((file, HclParser.Parse(file.Text)));
            }
            catch (HclSyntaxException ex)
            {
                findings.Add(new LintFinding("TF000", "invalid-hcl", Severity.Error,
                    $"The file could not be parsed: {ex.Message}", ex.Line, File: file.Path));
            }
        }

        CheckProviders(parsed, findings);

        var awsDefaultTags = parsed.Any(p => p.Body.BlocksOfType("provider")
            .Any(b => b.Labels is ["aws", ..] && b.Body.BlocksOfType("default_tags").Any()));

        foreach (var (file, body) in parsed)
        {
            foreach (var module in body.BlocksOfType("module"))
            {
                CheckModule(module, file, findings);
            }

            foreach (var resource in body.BlocksOfType("resource").Where(b => b.Labels.Count == 2))
            {
                var context = new ResourceContext(resource.Labels[0], $"{resource.Labels[0]}.{resource.Labels[1]}", file.Path, findings);
                CheckOpenIngress(resource, context);
                CheckPublicExposure(resource, context);
                CheckTags(resource, context, awsDefaultTags);
            }
        }

        return new LintReport(Name, findings);
    }

    private sealed record ResourceContext(string Type, string Address, string? File, List<LintFinding> Findings)
    {
        public void Add(string ruleId, string rule, Severity severity, string message, int line) =>
            Findings.Add(new LintFinding(ruleId, rule, severity, message, line, Address, File));
    }

    // ---- TF001/TF002: providers ----

    private static void CheckProviders(List<(TerraformFile File, HclBody Body)> parsed, List<LintFinding> findings)
    {
        var declared = new Dictionary<string, (HclAttribute Attribute, string? File)>(StringComparer.Ordinal);
        foreach (var (file, body) in parsed)
        {
            foreach (var requirements in body.BlocksOfType("terraform").SelectMany(t => t.Body.BlocksOfType("required_providers")))
            {
                foreach (var attribute in requirements.Body.Attributes)
                {
                    declared.TryAdd(attribute.Name, (attribute, file.Path));
                }
            }
        }

        foreach (var (name, (attribute, file)) in declared)
        {
            // Legacy form: aws = "~> 5.0". Current form: aws = { source = "hashicorp/aws", version = "~> 5.0" }.
            var version = HclValues.String(attribute.Expression) ??
                          HclValues.String(HclParser.ParseObject(attribute.Expression, attribute.Line).Attribute("version")?.Expression);

            if (string.IsNullOrWhiteSpace(version))
            {
                findings.Add(new LintFinding("TF001", "unpinned-provider", Severity.Warning,
                    $"Provider '{name}' has no version constraint, so 'terraform init' installs whatever version is newest. Add version = \"~> X.Y\".",
                    attribute.Line, $"provider {name}", file));
            }
            else if (!HasUpperBound(version))
            {
                findings.Add(new LintFinding("TF002", "unbounded-provider-version", Severity.Info,
                    $"Provider '{name}' is constrained to '{version}', which accepts any future major version. Prefer '~>' to stay within a major version.",
                    attribute.Line, $"provider {name}", file));
            }
        }

        var used = new Dictionary<string, (int Line, string? File)>(StringComparer.Ordinal);
        foreach (var (file, body) in parsed)
        {
            foreach (var block in body.Blocks)
            {
                var provider = block switch
                {
                    { Type: "provider", Labels: [var name, ..] } => name,
                    { Type: "resource" or "data", Labels: [var type, ..] } => type.Split('_')[0],
                    _ => null,
                };

                if (provider is not null && provider != "terraform")
                {
                    used.TryAdd(provider, (block.Line, file.Path));
                }
            }
        }

        foreach (var (provider, (line, file)) in used.Where(u => !declared.ContainsKey(u.Key)))
        {
            findings.Add(new LintFinding("TF001", "unpinned-provider", Severity.Warning,
                $"Provider '{provider}' is used but not listed in terraform {{ required_providers }}, so its version is not pinned.",
                line, $"provider {provider}", file));
        }
    }

    /// <summary>True when a constraint such as <c>~&gt; 5.0</c>, <c>= 5.1.0</c> or <c>&gt;= 5.0, &lt; 6.0</c> rules out a future major version.</summary>
    private static bool HasUpperBound(string constraint) =>
        constraint.Split(',').Select(part => part.Trim()).Any(part =>
            part.StartsWith("~>", StringComparison.Ordinal) ||
            part.StartsWith('<') ||
            part.StartsWith('=') ||
            (part.Length > 0 && char.IsDigit(part[0])));

    // ---- TF003: modules ----

    private static void CheckModule(HclBlock module, TerraformFile file, List<LintFinding> findings)
    {
        var source = HclValues.String(module.Body.Attribute("source")?.Expression);
        if (source is null || source.StartsWith("./", StringComparison.Ordinal) || source.StartsWith("../", StringComparison.Ordinal))
        {
            return;
        }

        var target = $"module.{module.Labels.FirstOrDefault() ?? "<unnamed>"}";
        var isGit = source.StartsWith("git::", StringComparison.Ordinal) || source.StartsWith("git@", StringComparison.Ordinal) ||
                    source.StartsWith("github.com/", StringComparison.Ordinal) || source.Contains(".git", StringComparison.Ordinal);

        if (isGit)
        {
            if (!source.Contains("ref=", StringComparison.Ordinal))
            {
                findings.Add(new LintFinding("TF003", "unpinned-module", Severity.Warning,
                    $"Module source '{source}' has no ?ref=, so it follows the default branch. Pin a tag or commit.",
                    module.Line, target, file.Path));
            }

            return;
        }

        var isRegistry = source.Split('/').Length is 3 or 4 && !source.Contains("://", StringComparison.Ordinal);
        if (isRegistry && module.Body.Attribute("version") is null)
        {
            findings.Add(new LintFinding("TF003", "unpinned-module", Severity.Warning,
                $"Registry module '{source}' has no version argument, so every 'terraform init' may pick up a new release.",
                module.Line, target, file.Path));
        }
    }

    // ---- TF010: ingress from anywhere ----

    private static void CheckOpenIngress(HclBlock resource, ResourceContext context)
    {
        var body = resource.Body;
        switch (context.Type)
        {
            case "aws_security_group":
                foreach (var ingress in body.BlocksOfType("ingress"))
                {
                    CheckAwsRule(ingress.Body, ingress.Line, context);
                }

                foreach (var ingress in body.BlocksOfType("dynamic").Where(d => d.Labels is ["ingress"]).SelectMany(d => d.Body.BlocksOfType("content")))
                {
                    CheckAwsRule(ingress.Body, ingress.Line, context);
                }

                break;
            case "aws_security_group_rule" when HclValues.String(body.Attribute("type")?.Expression) == "ingress":
                CheckAwsRule(body, resource.Line, context);
                break;
            case "aws_vpc_security_group_ingress_rule":
                var cidr = HclValues.String(body.Attribute("cidr_ipv4")?.Expression) ?? HclValues.String(body.Attribute("cidr_ipv6")?.Expression);
                if (cidr is AnyIPv4 or AnyIPv6)
                {
                    ReportOpen(context, cidr, AwsPorts(body), resource.Line);
                }

                break;
            case "azurerm_network_security_rule":
                CheckAzureRule(body, resource.Line, context);
                break;
            case "azurerm_network_security_group":
                foreach (var rule in body.BlocksOfType("security_rule"))
                {
                    CheckAzureRule(rule.Body, rule.Line, context);
                }

                break;
            case "google_compute_firewall":
                var direction = HclValues.String(body.Attribute("direction")?.Expression) ?? "INGRESS";
                var ranges = HclValues.StringList(body.Attribute("source_ranges")?.Expression);
                if (direction.Equals("INGRESS", StringComparison.OrdinalIgnoreCase) && ranges.FirstOrDefault(r => r is AnyIPv4 or AnyIPv6) is { } open)
                {
                    var allows = body.BlocksOfType("allow").ToArray();
                    var ports = allows.Length == 0 || allows.Any(a => a.Body.Attribute("ports") is null)
                        ? [PortRange.All]
                        : allows.SelectMany(a => HclValues.StringList(a.Body.Attribute("ports")?.Expression)).Select(PortRange.Parse).ToArray();
                    ReportOpen(context, open, ports, body.Attribute("source_ranges")!.Line);
                }

                break;
        }
    }

    private static void CheckAwsRule(HclBody rule, int line, ResourceContext context)
    {
        var cidrs = HclValues.StringList(rule.Attribute("cidr_blocks")?.Expression)
            .Concat(HclValues.StringList(rule.Attribute("ipv6_cidr_blocks")?.Expression));
        if (cidrs.FirstOrDefault(c => c is AnyIPv4 or AnyIPv6) is { } open)
        {
            ReportOpen(context, open, AwsPorts(rule), rule.Attribute("cidr_blocks")?.Line ?? line);
        }
    }

    private static PortRange[] AwsPorts(HclBody rule)
    {
        var protocol = HclValues.String(rule.Attribute("protocol")?.Expression) ?? HclValues.String(rule.Attribute("ip_protocol")?.Expression);
        if (protocol is "-1" or "all")
        {
            return [PortRange.All];
        }

        var from = HclValues.Int(rule.Attribute("from_port")?.Expression);
        var to = HclValues.Int(rule.Attribute("to_port")?.Expression) ?? from;
        return from is null ? [] : [new PortRange(from.Value, to!.Value)];
    }

    private static void CheckAzureRule(HclBody rule, int line, ResourceContext context)
    {
        var inbound = string.Equals(HclValues.String(rule.Attribute("direction")?.Expression), "Inbound", StringComparison.OrdinalIgnoreCase);
        var allow = string.Equals(HclValues.String(rule.Attribute("access")?.Expression), "Allow", StringComparison.OrdinalIgnoreCase);
        var source = HclValues.String(rule.Attribute("source_address_prefix")?.Expression) ??
                     HclValues.StringList(rule.Attribute("source_address_prefixes")?.Expression).FirstOrDefault(AnySource.Contains);
        if (!inbound || !allow || source is null || !AnySource.Contains(source))
        {
            return;
        }

        var ranges = HclValues.StringList(rule.Attribute("destination_port_range")?.Expression)
            .Concat(HclValues.StringList(rule.Attribute("destination_port_ranges")?.Expression))
            .Select(PortRange.Parse)
            .ToArray();
        ReportOpen(context, source, ranges, line);
    }

    private static void ReportOpen(ResourceContext context, string source, IReadOnlyList<PortRange> ports, int line)
    {
        var all = ports.Any(p => p.IsAll);
        var known = ports.Where(p => !p.IsUnknown).ToArray();
        var webOnly = known.Length > 0 && known.Length == ports.Count && known.All(p => p.From == p.To && WebPorts.Contains(p.From));
        var severity = all || ports.Any(p => AdminPorts.Any(p.Contains)) ? Severity.Error : webOnly ? Severity.Info : Severity.Warning;

        var what = all ? "every port" : known.Length == 0 ? "the configured ports" : $"{(known.Length == 1 && known[0].From == known[0].To ? "port" : "ports")} {string.Join(", ", known)}";
        var advice = severity switch
        {
            Severity.Error => "Restrict the source to known ranges, or reach admin ports through a bastion, VPN or SSM instead.",
            Severity.Info => "That is expected for a public load balancer or CDN origin; make sure that is what this is.",
            _ => "Make sure this is a public entry point such as a load balancer, and restrict the source otherwise.",
        };
        context.Add("TF010", "open-ingress", severity, $"Ingress from {source} allows {what} from the whole internet. {advice}", line);
    }

    // ---- TF011: public IPs and endpoints ----

    private static void CheckPublicExposure(HclBlock resource, ResourceContext context)
    {
        var body = resource.Body;
        switch (context.Type)
        {
            case "aws_instance" when IsTrue(body, "associate_public_ip_address"):
                context.Add("TF011", "public-ip", Severity.Warning,
                    "associate_public_ip_address = true gives the instance a public IP. Put it in a private subnet behind a load balancer or NAT.",
                    body.Attribute("associate_public_ip_address")!.Line);
                break;
            case "aws_launch_template":
                foreach (var nic in body.BlocksOfType("network_interfaces").Where(n => IsTrue(n.Body, "associate_public_ip_address")))
                {
                    context.Add("TF011", "public-ip", Severity.Warning,
                        "The launch template's network interface gets a public IP. Launch instances into private subnets instead.",
                        nic.Body.Attribute("associate_public_ip_address")!.Line);
                }

                break;
            case "aws_subnet" when IsTrue(body, "map_public_ip_on_launch"):
                context.Add("TF011", "public-ip", Severity.Warning,
                    "map_public_ip_on_launch = true gives every instance launched in this subnet a public IP.",
                    body.Attribute("map_public_ip_on_launch")!.Line);
                break;
            case "aws_db_instance" or "aws_rds_cluster_instance" when IsTrue(body, "publicly_accessible"):
                context.Add("TF011", "public-ip", Severity.Error,
                    "publicly_accessible = true gives the database a public endpoint. Keep databases private and connect through the VPC.",
                    body.Attribute("publicly_accessible")!.Line);
                break;
            case "azurerm_public_ip":
                context.Add("TF011", "public-ip", Severity.Warning,
                    "This creates a public IP address. Make sure it belongs to a load balancer or gateway, not to a VM's NIC.",
                    resource.Line);
                break;
            case "google_compute_instance":
                foreach (var access in body.BlocksOfType("network_interface").SelectMany(n => n.Body.BlocksOfType("access_config")))
                {
                    context.Add("TF011", "public-ip", Severity.Warning,
                        "An access_config block gives the instance an external IP. Remove it and use Cloud NAT or IAP for outbound and admin access.",
                        access.Line);
                }

                break;
        }
    }

    private static bool IsTrue(HclBody body, string attribute) => HclValues.Bool(body.Attribute(attribute)?.Expression) == true;

    // ---- TF020: tags ----

    private static void CheckTags(HclBlock resource, ResourceContext context, bool awsDefaultTags)
    {
        if (!TaggableTypes.Contains(context.Type))
        {
            return;
        }

        var attribute = context.Type.StartsWith("google_", StringComparison.Ordinal) ? "labels" : "tags";
        if (resource.Body.Attribute(attribute) is not null || resource.Body.BlocksOfType("tag").Any())
        {
            return;
        }

        if (awsDefaultTags && context.Type.StartsWith("aws_", StringComparison.Ordinal))
        {
            return;
        }

        context.Add("TF020", "missing-tags", Severity.Warning,
            $"No {attribute}, so the resource cannot be attributed to an owner, environment or cost center. Add {attribute}" +
            (context.Type.StartsWith("aws_", StringComparison.Ordinal) ? ", or set default_tags on the aws provider." : "."),
            resource.Line);
    }

    /// <summary>An inclusive port range; <see cref="All"/> for every port, <see cref="Unknown"/> when it cannot be read.</summary>
    private readonly record struct PortRange(int From, int To)
    {
        public static readonly PortRange All = new(0, 65535);
        public static readonly PortRange Unknown = new(-1, -1);

        public bool IsAll => From <= 0 && To >= 65535;

        public bool IsUnknown => From < 0;

        public bool Contains(int port) => !IsUnknown && port >= From && port <= To;

        /// <summary>Reads <c>22</c>, <c>8000-8080</c> or <c>*</c>.</summary>
        public static PortRange Parse(string text)
        {
            if (text.Trim() == "*")
            {
                return All;
            }

            var parts = text.Split('-', 2);
            return int.TryParse(parts[0], out var from)
                ? new PortRange(from, parts.Length == 2 && int.TryParse(parts[1], out var to) ? to : from)
                : Unknown;
        }

        public override string ToString() => From == To ? From.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{From}-{To}";
    }
}
