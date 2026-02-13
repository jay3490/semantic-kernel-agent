using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Agent.Core.Linting;

/// <summary>
/// Lints Kubernetes manifests (one or many YAML documents, including <c>kind: List</c>) for common
/// reliability and security problems in workload pod specs.
/// </summary>
public sealed class KubernetesManifestLinter
{
    public const string Name = "kubernetes";

    private static readonly HashSet<string> ClusterScopedKinds = new(StringComparer.Ordinal)
    {
        "Namespace", "Node", "PersistentVolume", "ClusterRole", "ClusterRoleBinding", "StorageClass",
        "CustomResourceDefinition", "PriorityClass", "IngressClass", "RuntimeClass", "CSIDriver",
        "MutatingWebhookConfiguration", "ValidatingWebhookConfiguration", "APIService", "VolumeSnapshotClass",
    };

    private static readonly HashSet<string> TemplatedWorkloadKinds = new(StringComparer.Ordinal)
    {
        "Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "ReplicationController", "Job",
    };

    public LintReport Lint(string yaml)
    {
        var findings = new List<LintFinding>();
        var stream = new YamlStream();

        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            findings.Add(new LintFinding("K8S000", "invalid-yaml", Severity.Error,
                $"The manifest is not valid YAML: {ex.Message}", LineOf(ex.Start)));
            return new LintReport(Name, findings);
        }

        foreach (var document in stream.Documents)
        {
            if (document.RootNode is YamlMappingNode root)
            {
                LintObject(root, findings);
            }
        }

        return new LintReport(Name, findings);
    }

    private static void LintObject(YamlMappingNode resource, List<LintFinding> findings)
    {
        var kind = Scalar(resource, "kind");
        if (kind is null)
        {
            return;
        }

        if (kind == "List")
        {
            foreach (var item in Child<YamlSequenceNode>(resource, "items")?.OfType<YamlMappingNode>() ?? [])
            {
                LintObject(item, findings);
            }

            return;
        }

        var metadata = Child<YamlMappingNode>(resource, "metadata");
        var name = (metadata is null ? null : Scalar(metadata, "name")) ?? "<unnamed>";
        var target = $"{kind}/{name}";

        if (!ClusterScopedKinds.Contains(kind) && (metadata is null || Scalar(metadata, "namespace") is null))
        {
            findings.Add(new LintFinding("K8S001", "missing-namespace", Severity.Info,
                "metadata.namespace is not set, so the object lands in whatever namespace the current kubectl context uses. Set it explicitly.",
                LineOf(resource.Start), target));
        }

        var podSpec = FindPodSpec(resource, kind);
        if (podSpec is null)
        {
            return;
        }

        var expectsProbes = ExpectsLongRunningContainers(kind, podSpec);

        foreach (var container in Containers(podSpec, "containers"))
        {
            LintContainer(container, target, checkProbes: expectsProbes, findings);
        }

        foreach (var container in Containers(podSpec, "initContainers"))
        {
            LintContainer(container, target, checkProbes: false, findings);
        }
    }

    private static void LintContainer(YamlMappingNode container, string owner, bool checkProbes, List<LintFinding> findings)
    {
        var target = $"{owner} container={Scalar(container, "name") ?? "<unnamed>"}";
        var line = LineOf(container.Start);

        var limits = Child<YamlMappingNode>(Child<YamlMappingNode>(container, "resources"), "limits");
        var missingLimits = new[] { "cpu", "memory" }.Where(r => limits is null || Scalar(limits, r) is null).ToArray();
        if (missingLimits.Length > 0)
        {
            findings.Add(new LintFinding("K8S002", "missing-resource-limits", Severity.Warning,
                $"resources.limits has no {string.Join(" or ", missingLimits)} limit, so one container can starve its neighbours on the node.",
                line, target));
        }

        if (checkProbes)
        {
            if (Child<YamlMappingNode>(container, "livenessProbe") is null)
            {
                findings.Add(new LintFinding("K8S003", "missing-liveness-probe", Severity.Warning,
                    "No livenessProbe, so a hung process is never restarted.", line, target));
            }

            if (Child<YamlMappingNode>(container, "readinessProbe") is null)
            {
                findings.Add(new LintFinding("K8S004", "missing-readiness-probe", Severity.Warning,
                    "No readinessProbe, so traffic can reach the pod before it is able to serve.", line, target));
            }
        }

        if (Child<YamlScalarNode>(container, "image") is { Value: { Length: > 0 } image } imageNode)
        {
            var reference = ImageReference.Parse(image);
            if (!reference.IsPinned)
            {
                findings.Add(new LintFinding("K8S005", "unpinned-image-tag", Severity.Warning,
                    $"Image {reference.DescribeUnpinned()}. Pin a version tag or digest so rollouts are reproducible.",
                    LineOf(imageNode.Start), target));
            }
        }

        var securityContext = Child<YamlMappingNode>(container, "securityContext");
        if (securityContext is not null && string.Equals(Scalar(securityContext, "privileged"), "true", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new LintFinding("K8S006", "privileged-container", Severity.Error,
                "securityContext.privileged is true, which gives the container full access to the host. Grant specific capabilities instead.",
                LineOf(securityContext.Start), target));
        }
    }

    private static YamlMappingNode? FindPodSpec(YamlMappingNode resource, string kind)
    {
        var spec = Child<YamlMappingNode>(resource, "spec");
        return kind switch
        {
            "Pod" => spec,
            "CronJob" => TemplateSpec(Child<YamlMappingNode>(Child<YamlMappingNode>(spec, "jobTemplate"), "spec")),
            _ when TemplatedWorkloadKinds.Contains(kind) => TemplateSpec(spec),
            _ => null,
        };

        static YamlMappingNode? TemplateSpec(YamlMappingNode? workloadSpec) =>
            Child<YamlMappingNode>(Child<YamlMappingNode>(workloadSpec, "template"), "spec");
    }

    /// <summary>Probes only make sense for containers that are meant to keep running.</summary>
    private static bool ExpectsLongRunningContainers(string kind, YamlMappingNode podSpec) => kind switch
    {
        "Job" or "CronJob" => false,
        "Pod" => Scalar(podSpec, "restartPolicy") is not ("Never" or "OnFailure"),
        _ => true,
    };

    private static IEnumerable<YamlMappingNode> Containers(YamlMappingNode podSpec, string key) =>
        Child<YamlSequenceNode>(podSpec, key)?.OfType<YamlMappingNode>() ?? [];

    private static T? Child<T>(YamlMappingNode? node, string key)
        where T : YamlNode =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child as T : null;

    private static string? Scalar(YamlMappingNode node, string key) =>
        Child<YamlScalarNode>(node, key)?.Value is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int LineOf(Mark mark) => (int)mark.Line;
}
