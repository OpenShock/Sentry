namespace OpenShock.Sentry.Models;

public sealed class OpenCvTemplateSettings
{
    public string TemplatePath { get; set; } = "";
    public float Threshold { get; set; } = 0.8f;
}