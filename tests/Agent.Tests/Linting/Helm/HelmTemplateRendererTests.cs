using Agent.Core.Linting.Helm;
using YamlDotNet.RepresentationModel;

namespace Agent.Tests.Linting.Helm;

public sealed class HelmTemplateRendererTests
{
    private static readonly HelmChartMetadata Chart = new("orders", "0.1.0", "2.4.1");

    private static HelmTemplateRenderer Renderer(string valuesYaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(valuesYaml));
        return new HelmTemplateRenderer(stream.Documents.Count == 0 ? null : (YamlMappingNode)stream.Documents[0].RootNode, Chart);
    }

    private static string Render(string template, string values = "{}") => Renderer(values).Render(template).Text;

    [Fact]
    public void Substitutes_values_chart_and_release_fields()
    {
        var text = Render("image: {{ .Values.image.repository }}:{{ .Chart.AppVersion }}\nname: {{ .Release.Name }}-{{ .Chart.Name }}\n",
            "image:\n  repository: ghcr.io/example/orders\n");

        Assert.Equal("image: ghcr.io/example/orders:2.4.1\nname: release-name-orders\n", text);
    }

    [Theory]
    [InlineData("tag: \"\"", "{{ .Values.tag | default .Chart.AppVersion }}", "2.4.1")]
    [InlineData("tag: \"1.0\"", "{{ .Values.tag | default .Chart.AppVersion }}", "1.0")]
    [InlineData("{}", "{{ default \"x\" .Values.missing }}", "x")]
    [InlineData("name: web", "{{ .Values.name | quote }}", "\"web\"")]
    [InlineData("name: web", "{{ .Values.name | upper | printf \"%s-svc\" }}", "WEB-svc")]
    [InlineData("name: averyveryverylongname", "{{ .Values.name | trunc 6 | trimSuffix \"v\" }}", "avery")]
    [InlineData("{}", "{{ include \"orders.fullname\" . }}", "orders.fullname")]
    [InlineData("{}", "{{ (default \"a\" .Values.x) | quote }}", "\"a\"")]
    public void Evaluates_pipelines_and_common_functions(string values, string template, string expected) =>
        Assert.Equal(expected, Render(template, values));

    [Fact]
    public void ToYaml_and_nindent_render_a_values_subtree_in_block_style()
    {
        const string values = """
            resources:
              limits:
                cpu: "1"
                memory: 512Mi
              args: [--port, "8080"]
            """;

        var text = Render("resources:\n  {{- toYaml .Values.resources | nindent 2 }}\n", values);

        Assert.Equal("resources:\n  limits:\n    cpu: \"1\"\n    memory: 512Mi\n  args:\n  - --port\n  - \"8080\"\n", text);
    }

    [Fact]
    public void Empty_collections_render_as_flow_style()
    {
        Assert.Equal("r: {}", Render("r: {{ toYaml .Values.resources }}", "resources: {}"));
        Assert.Equal("l: []", Render("l: {{ toYaml .Values.list }}", "list: []"));
    }

    [Theory]
    [InlineData("enabled: true", "yes")]
    [InlineData("enabled: false", "no")]
    [InlineData("{}", "no")]
    public void If_renders_the_first_true_branch(string values, string expected) =>
        Assert.Equal(expected, Render("{{ if .Values.enabled }}yes{{ else }}no{{ end }}", values));

    [Fact]
    public void Else_if_chains_are_followed()
    {
        const string template = "{{ if eq .Values.mode \"a\" }}A{{ else if eq .Values.mode \"b\" }}B{{ else }}C{{ end }}";

        Assert.Equal("B", Render(template, "mode: b"));
        Assert.Equal("C", Render(template, "mode: z"));
    }

    [Fact]
    public void With_changes_the_dot_and_skips_empty_values()
    {
        Assert.Equal("port: 80", Render("{{ with .Values.service }}port: {{ .port }}{{ end }}", "service:\n  port: 80"));
        Assert.Equal("", Render("{{ with .Values.service }}port: {{ .port }}{{ end }}", "{}"));
    }

    [Fact]
    public void Range_repeats_the_body_for_list_items_and_binds_variables()
    {
        const string values = """
            env:
              - name: A
                value: "1"
              - name: B
                value: "2"
            ports:
              http: 80
              grpc: 81
            """;

        Assert.Equal("A=1;B=2;", Render("{{ range .Values.env }}{{ .name }}={{ .value }};{{ end }}", values));
        Assert.Equal("http:80 grpc:81 ", Render("{{ range $name, $port := .Values.ports }}{{ $name }}:{{ $port }} {{ end }}", values));
        Assert.Equal("none", Render("{{ range .Values.missing }}x{{ else }}none{{ end }}", values));
    }

    [Fact]
    public void Variables_and_root_access_work_inside_blocks()
    {
        const string template = "{{- $repo := .Values.image.repository -}}{{ range .Values.tags }}{{ $repo }}:{{ . }} {{ $.Chart.Name }} {{ end }}";

        Assert.Equal("nginx:1 orders nginx:2 orders ", Render(template, "image:\n  repository: nginx\ntags: [\"1\", \"2\"]"));
    }

    [Fact]
    public void Trim_markers_remove_surrounding_whitespace_and_comments_render_nothing()
    {
        Assert.Equal("a:\nb", Render("a:\n  {{- /* a comment */}}\nb"));
        Assert.Equal("ab", Render("a  {{- \"\" -}}  \n  b"));
    }

    [Fact]
    public void Defines_are_not_rendered_in_place()
    {
        Assert.Equal("kind: Service\n", Render("{{- define \"x\" }}hidden{{ end -}}\nkind: Service\n"));
    }

    [Fact]
    public void Unknown_functions_render_nothing_but_count_as_true()
    {
        Assert.Equal("on:", Render("{{ if lookup \"v1\" \"Secret\" }}on{{ end }}:{{ lookup \"v1\" \"Secret\" }}"));
    }

    [Fact]
    public void Every_rendered_line_maps_back_to_the_template_line_that_produced_it()
    {
        const string template = """
            kind: Deployment
            spec:
              {{- if .Values.replicas }}
              replicas: {{ .Values.replicas }}
              {{- end }}
              resources:
                {{- toYaml .Values.resources | nindent 4 }}
              done: true
            """;

        var rendered = Renderer("replicas: 2\nresources:\n  limits:\n    cpu: 1\n    memory: 1Gi").Render(template);

        var lines = rendered.Text.Split('\n');
        Assert.Equal("  replicas: 2", lines[2]);
        Assert.Equal(4, rendered.SourceLineOf(3));
        Assert.Equal("    limits:", lines[4]);
        Assert.Equal([7, 7, 7], new[] { 5, 6, 7 }.Select(rendered.SourceLineOf));
        Assert.Equal("  done: true", lines[7]);
        Assert.Equal(8, rendered.SourceLineOf(8));
    }

    [Theory]
    [InlineData("{{ if .Values.x }}never closed")]
    [InlineData("{{ end }}")]
    [InlineData("{{ .Values.x ")]
    [InlineData("{{ \"unterminated }}")]
    public void Malformed_templates_raise_a_template_exception(string template) =>
        Assert.Throws<HelmTemplateException>(() => Render(template));
}
